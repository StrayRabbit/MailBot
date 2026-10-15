using MailBot.Core.Logging;
using MailBot.Core.Models;
using MailBot.Core.Scheduling;
using MailBot.Core.Senders;
using MailBot.Core.Sending;
using MailBot.Core.Storage;

namespace MailBot.Core.Verification;

/// <summary>
/// 后台 worker：定期轮询所有启用的验证邮箱，根据主题前缀匹配推进批次状态机。
/// 设计要点：
///   - 启动时做"冷启动同步阶段"：一次性同步拉一次，避免老批次被错误判 Unconfirmed（§6.5）
///   - 主循环：拉取 → 匹配 → 超时判定 → sleep
///   - 状态推进时同步落库 + 触发事件
///
/// 状态机：
///   - 首查（> RecheckIntervalSeconds）：IMAP 命中 → Verified；未命中 → CheckAttempts=1
///   - 后续重查：IMAP 命中 → Verified；仍未命中 → CheckAttempts++
///   - CheckAttempts 达到 MaxPollAttempts → Unconfirmed（回退 pending + 账号冷却）
/// </summary>
public sealed class VerificationWorker : IDisposable
{
    private readonly VerifierRepository _verifierRepo;
    private readonly RecipientRepository _recipientRepo;
    private readonly SenderRepository _senderRepo;
    private readonly SenderPool _senderPool;
    private readonly VerifierConfig _config;
    private readonly IImapPoller _imap;
    private IResendHandler? _resendHandler;
    private readonly Action<string> _log;
    private readonly ILogger? _uiLogger;

    private readonly List<BatchTracker> _active = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event EventHandler<BatchTracker>? BatchVerified;
    public event EventHandler<BatchTracker>? BatchUnconfirmed;

    public VerificationWorker(
        VerifierRepository verifierRepo,
        RecipientRepository recipientRepo,
        SenderRepository senderRepo,
        SenderPool senderPool,
        VerifierConfig config,
        IImapPoller imap,
        Action<string> log,
        IResendHandler? resendHandler = null,
        ILogger? uiLogger = null)
    {
        _verifierRepo = verifierRepo;
        _recipientRepo = recipientRepo;
        _senderRepo = senderRepo;
        _senderPool = senderPool;
        _config = config;
        _imap = imap;
        _resendHandler = resendHandler;
        _log = log;
        _uiLogger = uiLogger;
    }

    /// <summary>
    /// 装配阶段后注入 IResendHandler：解决 worker 与 SendEngine 之间的循环依赖（worker 订阅 SendEngine 的事件，
    /// 但 SendEngine 又持有 worker 引用来 register batch）。
    /// </summary>
    public void AttachResendHandler(IResendHandler handler)
    {
        _resendHandler = handler;
    }

    /// <summary>对外暴露的活动批次（只读）。测试用。</summary>
    public IReadOnlyList<BatchTracker> ActiveBatches => _active;

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(async () =>
        {
            await ColdStartAsync(_cts.Token);
            await LoopAsync(_cts.Token);
        }, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    /// <summary>SendEngine 发完一批后调用。</summary>
    public void RegisterBatch(BatchTracker batch)
    {
        lock (_active) _active.Add(batch);
    }

    private async Task ColdStartAsync(CancellationToken ct)
    {
        // 从 SQLite 加载所有 unverified 的 recipient，重建批次追踪器
        var unverified = _recipientRepo.GetByStatus("unverified", limit: 10000);
        if (unverified.Count == 0) return;

        var grouped = unverified.GroupBy(r => r.BatchId ?? string.Empty)
            .Where(g => !string.IsNullOrEmpty(g.Key));
        foreach (var g in grouped)
        {
            var first = g.First();
            var maxAttempts = g.Max(r => r.UnconfirmedCount);
            var tracker = new BatchTracker(
                g.Key,
                first.LastAttemptAt ?? DateTime.UtcNow,
                first.SentViaSenderId ?? 0,
                g.Select(r => r.Id).ToList(),
                initialUnconfirmedAttempts: maxAttempts);
            lock (_active) _active.Add(tracker);
        }

        // 同步拉一次（不让老批次被立即判超时）
        // 兼容旧 LookbackHours=1 行为：传 now - LookbackHours 给 IMAP，后续 PollOnceAsync 改为按 SentAt 起点。
        // 这里 ColdStart 阶段沿用原"全局窗口"语义，避免破坏冷启动边界。
        await PollOnceAsync(ct, isColdStart: true);
        _log($"[冷启动] 已恢复 {unverified.Count} 个未完成收件人，批次数={grouped.Count()}");
        _uiLogger?.Emit(LogLevel.Info, $"冷启动恢复 {unverified.Count} 个未完成收件人，{grouped.Count()} 个批次");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct, isColdStart: false);
            }
            catch (Exception ex)
            {
                _log($"[worker异常] {ex.GetType().Name}: {ex.Message}");
                _uiLogger?.Emit(LogLevel.Error, $"worker 异常 {ex.GetType().Name}：{ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds), ct);
            }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct, bool isColdStart)
    {
        var allVerifiers = _verifierRepo.ListEnabled();
        if (allVerifiers.Count == 0) return;

        // 收集所有 active 批次的 id → tracker 映射
        Dictionary<string, BatchTracker> pending;
        lock (_active)
        {
            pending = _active.Where(b => b.IsPending)
                .ToDictionary(b => b.BatchId, b => b, StringComparer.Ordinal);
        }
        if (pending.Count == 0) return;

        var now = DateTime.UtcNow;
        var lookbackWindow = TimeSpan.FromMinutes(_config.LookbackMinutes);
        var recheckWindow = TimeSpan.FromSeconds(_config.RecheckIntervalSeconds);

        // 按批次遍历：每个 batch 用自己的 SentAt 作为 IMAP 查询起点
        foreach (var (batchId, tracker) in pending)
        {
            if (ct.IsCancellationRequested) return;

            // 兜底：批次存活超过 LookbackMinutes → 强制 Unconfirmed
            // 用 ResentAt ?? SentAt：换 verifier 重发后以新重发时刻作为轮询窗口起点。
            var effectiveSentAt = tracker.ResentAt ?? tracker.SentAt;
            var elapsed = now - effectiveSentAt;
            if (elapsed > lookbackWindow)
            {
                ForceUnconfirmedBatch(tracker);
                continue;
            }

            // 还没到首查间隔（默认 180s = 3 分钟），本轮不查
            if (elapsed < recheckWindow) continue;

            var sender = _senderRepo.GetById(tracker.SenderId);
            var senderEmail = sender?.Email;
            if (senderEmail is null)
            {
                // 找不到发件账号（极少见：账号已被删），直接 Unconfirmed
                ForceUnconfirmedBatch(tracker);
                continue;
            }

            // 查询起点 = 批次 SentAt - 2 分钟容差
            // 为什么是 SentAt - 2min 而不是 SentAt：
            //   SentAt 是 SMTP 250 OK 之后用 DateTime.UtcNow 写库的；
            //   而 QQ IMAP 服务端在 SMTP 投递时就已经给邮件打 INTERNALDATE（+08:00 偏移）。
            //   实测 InternalDate 比 SentAt 早 13 秒是常态。
            //   用"严格 >= SentAt"会漏掉真实 BCC。
            // 为什么是 -2min 而不是去掉过滤：
            //   IMAP SINCE 是 day-precision，同一天内多批 BCC 会让老批次邮件也被返回；
            //   保留客户端过滤可以把"跨批次误命中"挡掉（老批次 InternalDate 距离 SentAt 通常 > 5min）。
            var sinceUtc = new DateTimeOffset(effectiveSentAt, TimeSpan.Zero) - TimeSpan.FromMinutes(2);

            // 候选 verifier = enabled 中"还未达到尝试上限"的全部（首发 verifier 也包含在首轮查询）：
            //   - 首发 verifier 是 BCC 收件方，必须先确认它是否真的收到 BCC
            //   - swap 之后用 ResentAt 推进轮询窗口，老 verifier 仍在 IMAP 范围内（不主动排除）
            // 当达到 MaxVerifierAttemptsPerBatch 上限，强制只查最后/未用的 verifier，避免无限循环
            var candidatesForBatch = tracker.AttemptedVerifierIds.Count < _config.MaxVerifierAttemptsPerBatch
                ? allVerifiers
                : allVerifiers.Where(v => !tracker.AttemptedVerifierIds.Contains(v.Id)).ToList();
            if (candidatesForBatch.Count == 0)
            {
                // 该批次已无候选 verifier，跳过本轮
                continue;
            }

            bool hit = false;
            foreach (var v in candidatesForBatch)
            {
                var items = await _imap.PollSinceAsync(v, sinceUtc, ct);
                // IMAP SINCE 是 day-precision：同一 GMT+8 日内多批 BCC 会让老批次邮件
                // 误命中当前批次的查询。客户端按 INTERNALDATE 二次过滤到 SentAt 之后。
                // INTERNALDATE 为 null 的服务端（如某些 Gmail 配置）放行——保留旧语义。
                if (items.Any(i =>
                        (i.InternalDate is null || i.InternalDate >= sinceUtc) &&
                        string.Equals(i.FromEmail, senderEmail, StringComparison.OrdinalIgnoreCase)))
                {
                    hit = true;
                    break;
                }
            }

            // 在锁外记录状态推进，避免阻塞 IMAP 拉取
            bool wasPending;
            int attemptsAfterIncrement = 0;
            lock (_active)
            {
                if (!tracker.IsPending) continue;
                wasPending = true;
                tracker.IncrementCheckAttempts();
                attemptsAfterIncrement = tracker.CheckAttempts;
                if (hit)
                {
                    tracker.MarkVerified();
                }
            }

            if (hit && wasPending)
            {
                _recipientRepo.MarkVerified(batchId, DateTime.UtcNow);
                // BCC 有验证模式："今日已发" 等于"已送达"——只有 IMAP 命中后才计入 sent_today。
                // 计入数量 = 本批次 BCC 收件人数（tracker.RecipientIds.Count，已在 BatchTracker 构造时写入）。
                // 无验证模式的批次没有 verifier 邮箱，SendEngine 已在发送成功时计入 sent_today，这里重复计入会导致双倍。
                //   通过 _senderPool 区分：tracker.AttemptedVerifierIds.Count > 0 表示走过 verifier 流程。
                //   但更稳的判断是：tracker 是否注册过 verifier id（首发 verifier 或重发 verifier）。
                if (tracker.AttemptedVerifierIds.Count > 0)
                {
                    var today = QuotaReset.Today(DateTime.UtcNow);
                    _senderRepo.IncrementSentToday(tracker.SenderId, tracker.RecipientIds.Count, today);
                }
                BatchVerified?.Invoke(this, tracker);
                _uiLogger?.Emit(LogLevel.Success,
                    $"已验证 (…{BatchIdFormatter.Short(batchId)})，用时 {elapsed.TotalSeconds:F0}s");
            }
            else if (wasPending)
            {
                // 未命中：先看是否已经走完 MaxPollAttempts 轮询节奏。
                // 设计点：首发 verifier 命中可能晚于其他 verifier（IMAP 索引延迟），
                // 不能一发完就立刻 swap 换下一个 verifier——要给首发 verifier 留够
                // MaxPollAttempts 轮轮询的机会（默认 3 次 × PollIntervalSeconds 60s ≈ 3 分钟）。
                // 只有走完轮询节奏仍 miss，才允许 swap 重发。
                // 否则只记 [check-miss]，让下一轮 PollOnceAsync 自然继续走节奏。
                if (tracker.CheckAttempts < _config.MaxPollAttempts)
                {
                    _log($"[check-miss] batch={batchId} 未命中发件账号 {senderEmail}（已等 {elapsed.TotalSeconds:F0}s，第 {attemptsAfterIncrement}/{_config.MaxPollAttempts} 次轮询）");
                    _uiLogger?.Emit(LogLevel.Info,
                        $"待重查 (…{BatchIdFormatter.Short(batchId)})，第 {attemptsAfterIncrement}/{_config.MaxPollAttempts} 次未命中");
                    continue;
                }

                // 走完 MaxPollAttempts 仍 miss：看是否能"换 verifier 重发"（重发达 MaxVerifierAttemptsPerBatch 上限才走 Unconfirmed）
                var verifierCount = tracker.AttemptedVerifierIds.Count;
                var canStillSwap = verifierCount < _config.MaxVerifierAttemptsPerBatch;
                if (canStillSwap && _resendHandler is not null)
                {
                    var nextVerifier = PickNextVerifier(allVerifiers, tracker.AttemptedVerifierIds);
                    if (nextVerifier is null)
                    {
                        // enabled verifier 全部已用过（极少见：enabled 数量 < MaxVerifierAttemptsPerBatch
                        // 且首发后又禁用了别的 verifier） → 走 Unconfirmed
                        ForceUnconfirmedBatch(tracker);
                    }
                    else
                    {
                        _log($"[verify-resend] batch={batchId} 切到 verifier={nextVerifier.Email} 重发整批");
                        bool ok;
                        try
                        {
                            ok = await _resendHandler.ResendBatchAsync(
                                tracker, nextVerifier.Id, ct);
                        }
                        catch (Exception ex)
                        {
                            _log($"[verify-resend]异常 {ex.GetType().Name}：{ex.Message}");
                            ok = false;
                        }

                        if (!ok)
                        {
                            // SMTP 失败或 sender/verifier 缺失 → 走 Unconfirmed
                            ForceUnconfirmedBatch(tracker);
                        }
                        else
                        {
                            // 重发成功：重置 CheckAttempts，让下一轮轮询重新走 MaxPollAttempts 次节奏
                            lock (_active)
                            {
                                if (tracker.IsPending) tracker.ResetCheckAttemptsForResend();
                            }
                            _uiLogger?.Emit(LogLevel.Info,
                                $"已重发，等待新 verifier 命中 (…{BatchIdFormatter.Short(batchId)})");
                        }
                    }
                }
                else
                {
                    // canStillSwap=false 且 CheckAttempts >= MaxPollAttempts → 走原 Unconfirmed 路径
                    ForceUnconfirmedBatch(tracker);
                }
            }
        }

        // 抑制 isColdStart 参数未使用警告（保留为公共方法约定）
        _ = isColdStart;
    }

    /// <summary>
    /// 从 enabled verifier 列表中选下一个"未在该批次用过的"。随机选择以分散命中概率；
    /// 若全部都已用过则返回 null。
    /// </summary>
    private static VerifierAccount? PickNextVerifier(
        List<VerifierAccount> enabled, IReadOnlyList<long> attempted)
    {
        var candidates = enabled.Where(v => !attempted.Contains(v.Id)).ToList();
        if (candidates.Count == 0) return null;
        return candidates[Random.Shared.Next(candidates.Count)];
    }

    /// <summary>
    /// 强制批次 Unconfirmed：用于批次存活超过 LookbackMinutes、或找不到发件账号、或达 MaxPollAttempts 仍未命中。
    /// 必须先在锁内检查 IsPending + 推状态机，再锁外做副作用（写库、触发事件）。
    /// 注：去掉了自动退额机制——IMAP 超时不再自动减扣 SentToday，由用户通过账号页"编辑已发"手动调整。
    /// </summary>
    private void ForceUnconfirmedBatch(BatchTracker tracker)
    {
        int attemptsForCooldown;
        int checkAttempts;
        bool wasPending;
        lock (_active)
        {
            if (!tracker.IsPending) return;
            wasPending = true;
            tracker.IncrementUnconfirmedAttempts();
            attemptsForCooldown = tracker.UnconfirmedAttempts;
            checkAttempts = tracker.CheckAttempts;
            tracker.MarkUnconfirmed();
        }
        if (!wasPending) return;

        var cooldownMin = _config.ComputeCooldownMinutes(attemptsForCooldown);
        var cooldown = TimeSpan.FromMinutes(cooldownMin);

        _recipientRepo.MarkUnconfirmedWithCooldown(tracker.BatchId, "验证邮箱超时未收到", cooldown);
        var resetCount = _recipientRepo.ResetToPendingByBatchId(tracker.BatchId);

        var sender = _senderRepo.GetById(tracker.SenderId);
        var senderEmail = sender?.Email;
        if (sender != null)
        {
            var consecutive = sender.ConsecutiveFailures + 1;
            _senderPool.SetCooling(sender.Id, cooldown, consecutive);
        }

        BatchUnconfirmed?.Invoke(this, tracker);
        _uiLogger?.Emit(LogLevel.Warning,
            $"验证超时 (…{BatchIdFormatter.Short(tracker.BatchId)})，账号 {senderEmail ?? tracker.SenderId.ToString()} 冷却 {cooldownMin}min；{resetCount} 个回退 pending；配额未自动调整，请到「账号」页手动核对",
            account: senderEmail);
    }
}