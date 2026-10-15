using MailBot.Core;
using MailBot.Core.Logging;
using MailBot.Core.Mail;
using MailBot.Core.Models;
using MailBot.Core.Scheduling;
using MailBot.Core.Senders;
using MailBot.Core.Storage;
using MailBot.Core.Verification;
using MimeKit;

namespace MailBot.Core.Sending;

/// <summary>
/// 引擎运行状态。
/// </summary>
public enum EngineState
{
    Idle,
    Running,
    Paused,
    Completed,
    Error,
}

/// <summary>
/// 单次批处理的执行摘要，供 UI 实时监控面板使用。
/// </summary>
public sealed class SendBatchReport
{
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public long SenderId { get; set; }
    public string SenderEmail { get; set; } = string.Empty;
    public string? BatchId { get; set; }
    public int BatchSize { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SenderStatusAfter { get; set; }

    /// <summary>
    /// 本批次 SMTP 调用的结果分类与错误信息。
    /// SendEngine 用它决定走 SuccessPath 还是 FailurePath,Classifier 用它决定账号新状态。
    /// BatchExecutor.ExecuteAsync 负责填充。
    /// </summary>
    public SmtpSendResult? SmtpResult { get; set; }

    /// <summary>本批次被选中的验证邮箱 BCC（仅一个）。未配置 verifier 时为 null。</summary>
    public string? VerifierEmail { get; set; }

    /// <summary>
    /// worker 计划首次拉取 IMAP 的 UTC 时刻（= SentAt + RecheckIntervalSeconds）。
    /// UI 用于显示"首查 19:24:07"，无需自己换算 RecheckIntervalSeconds。
    /// 无验证模式（VerifierEmail 为 null）时也为 null。
    /// </summary>
    public DateTime? FirstCheckAtUtc { get; set; }
}

/// <summary>
/// 主发送循环。设计文档 6.2 节：
///   - 时间窗口外：睡到下个窗口起点
///   - 没有可用账号：睡到次日 00:00
///   - 没有 pending 收件人：标记 Completed，退出循环
///   - 否则：取一批 → SMTP 发送 → 落库 → 间隔 sleep
/// </summary>
public sealed class SendEngine : IResendHandler
{
    private readonly Database _db;
    private readonly SenderRepository _senderRepo;
    private readonly RecipientRepository _recipientRepo;
    private readonly SenderPool _senderPool;
    private readonly SmtpDispatcher _dispatcher;
    private readonly VerifierRepository _verifierRepo;
    private readonly VerificationWorker _verificationWorker;
    private readonly VerifierConfig _verifierConfig;
    private readonly BatchExecutor _executor;
    private readonly SenderFailureClassifier _classifier;
    private readonly ILogger? _uiLogger;

    private BatchTracker? _lastBatch;
    private MailTemplate? _currentTemplate;
    private CancellationTokenSource? _runCts;

    public SendEngine(
        Database db,
        SenderRepository senderRepo,
        RecipientRepository recipientRepo,
        SenderPool senderPool,
        SmtpDispatcher dispatcher,
        VerifierRepository verifierRepo,
        VerificationWorker verificationWorker,
        VerifierConfig verifierConfig,
        BatchExecutor executor,
        SenderFailureClassifier classifier,
        ILogger? uiLogger = null)
    {
        _db = db;
        _senderRepo = senderRepo;
        _recipientRepo = recipientRepo;
        _senderPool = senderPool;
        _dispatcher = dispatcher;
        _verifierRepo = verifierRepo;
        _verificationWorker = verificationWorker;
        _verifierConfig = verifierConfig;
        _executor = executor;
        _classifier = classifier;
        _uiLogger = uiLogger;

        // 监听送达验证两类终态事件：收到任一终态即清空 _lastBatch，
        // 避免主循环因 _lastBatch.IsPending 永远 true 而卡死。
        _verificationWorker.BatchVerified += OnBatchTerminal;
        _verificationWorker.BatchUnconfirmed += OnBatchTerminal;
    }

    private void OnBatchTerminal(object? sender, BatchTracker batch)
    {
        if (_lastBatch?.BatchId == batch.BatchId)
        {
            _lastBatch = null;
        }
    }

    public EngineState State { get; private set; } = EngineState.Idle;
    public string? CurrentSenderEmail { get; private set; }

    /// <summary>单次发送完成时触发，UI 监控面板订阅。</summary>
    public event EventHandler<SendBatchReport>? BatchCompleted;

    /// <summary>下一批等待信息变化时触发。UI 状态栏订阅同步更新。</summary>
    public event EventHandler<string>? NextBatchHintChanged;

    /// <summary>引擎状态变化时触发。</summary>
    public event EventHandler<EngineState>? StateChanged;

    /// <summary>日志行（信息级别），UI 日志面板订阅。</summary>
    public event EventHandler<string>? Logged;

    /// <summary>
    /// 主循环入口。cancellationToken 由 UI "停止"按钮触发。
    /// </summary>
    public async Task RunAsync(MailTemplate template, AppConfig config, CancellationToken cancellationToken)
    {
        if (State == EngineState.Running) return;
        SetState(EngineState.Running);

        _currentTemplate = template;

        // 把外层 token 与引擎自管的 _runCts 链接：任一取消都会打断主循环，
        // 这样 Stop() / Dispose() 不需要外部持有 token 也能停掉 RunAsync。
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _runCts.Token;

        var window = new TimeWindow(config.WindowStart, config.WindowEnd);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.Now;

            // 1. 时间窗口判断
            if (!window.IsInWindow(now))
            {
                var nextStart = window.NextStart(now);
                Log($"不在发送窗口内，下次开始：{nextStart:yyyy-MM-dd HH:mm}");
                SetNextBatchHint("跨出窗口", nextStart - now, detail: $"明日 {nextStart:HH:mm}");
                await SleepUntil(nextStart, ct);
                continue;
            }

            // 1.5 策略 C：上一批等待送达验证（避免上一批超时未确认期间继续发下一批）
            if (_lastBatch is { IsPending: true } last
                && (DateTime.UtcNow - last.SentAt).TotalSeconds > _verifierConfig.SendHoldOnUnverifiedSeconds)
            {
                SetNextBatchHint("等待验证", TimeSpan.FromSeconds(_verifierConfig.SendHoldOnUnverifiedSeconds),
                    detail: $"已等 {(DateTime.UtcNow - last.SentAt).TotalSeconds:F0}s");
                await WaitForBatchAsync(last, ct);
                if (last.State == BatchState.Unconfirmed)
                {
                    Log($"上一批 {last.BatchId} 确认失败，账号已冷却，继续选账号");
                    _uiLogger?.Emit(LogLevel.Warning, $"上一批 (…{BatchIdFormatter.Short(last.BatchId)}) 确认失败，账号已冷却");
                    _lastBatch = null;
                }
            }

            // 2. 选账号
            await ResetQuotaIfNeededAsync(now);
            var sender = _senderPool.PickBestSender(now);
            CurrentSenderEmail = sender?.Email;

            if (sender is null)
            {
                // 不再睡到明天 00:00：取"最早一个账号重新可用"的时刻（冷却到期 / 配额重置）。
                // 通常就是被 60min 冷却的账号到期时间。
                var allSenders = _senderRepo.GetAll();
                var earliest = SenderPool.GetEarliestRetryTime(allSenders, now);
                var retryAt = earliest ?? QuotaReset.NextReset(now);
                var wait = retryAt - now;
                var reason = wait.TotalHours >= 12 ? "次日重置" : "冷却到期";
                Log($"所有账号都不可用，{reason}后重试：{retryAt:yyyy-MM-dd HH:mm}（约 {wait.TotalMinutes:F0} 分钟后）");
                SetNextBatchHint(reason, wait, detail: retryAt.ToString("HH:mm"));
                await SleepUntil(retryAt, ct);
                continue;
            }

            // 3. 拉一批
            var batch = _recipientRepo.PickPending(config.BatchSize);
            if (batch.Count == 0)
            {
                Log("所有待发送邮件已完成，引擎停止");
                SetState(EngineState.Completed);
                CurrentSenderEmail = null;
                return;
            }

            // 4. 调 BatchExecutor 串完整批(邮箱预校验 / 选 verifier / 装 MimeMessage / SMTP 重试 / 落库 / 计入)
            var report = await _executor.ExecuteAsync(sender, template, batch, ct);

            // 5. 根据 executor 的结果分类处理:成功 vs 失败
            if (report.SmtpResult is { IsSuccess: true })
            {
                // 成功:executor 已 MarkUnverified(MarkSent) + (无 verifier 路径)计入配额
                if (!string.IsNullOrEmpty(report.VerifierEmail) && !string.IsNullOrEmpty(report.BatchId))
                {
                    // 有 verifier 模式:把 tracker 注册到 worker 等 IMAP 命中
                    var tracker = new BatchTracker(
                        report.BatchId, DateTime.UtcNow, sender.Id,
                        batch.Select(r => r.Id).ToList());
                    _verificationWorker.RegisterBatch(tracker);
                    _lastBatch = tracker;
                    Log($"[成功] {sender.Email} → {report.SuccessCount} 封 BCC + 1 封 verifier BCC (batch={report.BatchId})，" +
                        $"最长 {_verifierConfig.LookbackMinutes}min 内未命中则账号冷却 {_verifierConfig.UnconfirmedCooldownMinutes}min 后重发");
                }
                else
                {
                    Log($"[成功] {sender.Email} → {report.SuccessCount} 封 BCC（无验证模式，按送达计）");
                }
                // 把账号拉回 normal(即使之前在 cooling,成功一次就放出来)
                _senderRepo.UpdateStatus(sender.Id, SenderStatus.Normal, consecutiveFailures: 0, cooldownUntil: null);
                report.SenderStatusAfter = SenderStatus.Normal;
            }
            else
            {
                // 失败:走 Classifier 决定账号新状态 + 收件人重试上限检查
                ApplyFailure(sender, report, config, batch);
            }

            BatchCompleted?.Invoke(this, report);

            // 6. 间隔 sleep
            try
            {
                var interval = config.SendIntervalSeconds > 0
                    ? TimeSpan.FromSeconds(config.SendIntervalSeconds)
                    : TimeSpan.FromSeconds(30);
                SetNextBatchHint("间隔", interval);
                await Task.Delay(interval, ct);
            }
            catch (TaskCanceledException) { return; }
        }

        SetState(EngineState.Idle);
        CurrentSenderEmail = null;
    }

    public void Pause() => SetState(EngineState.Paused);

    public void Resume()
    {
        if (State == EngineState.Paused) SetState(EngineState.Running);
    }

    /// <summary>
    /// 主动停止正在运行的引擎：取消内部 CTS，主循环会在下一个 await 退出。
    /// 幂等,无 RunAsync 进行中时调用为 no-op。
    /// </summary>
    public void Stop()
    {
        _runCts?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        _runCts?.Dispose();
        _runCts = null;
    }

    /// <summary>
    /// IResendHandler 实现：换 verifier 重发整批 BCC。
    /// 设计要点：
    ///   - 不扣配额（tracker.ChargedQuotaCount 保持首次计入值；最终 Unconfirmed 仍按该值退一次）。
    ///   - 不触发 BatchCompleted 事件（避免污染监控面板"等待验证中"提示）。
    ///   - 不调 _lastBatch、不动 SendEngine 主循环状态。
    ///   - 不冷却账号（仅 Unconfirmed 路径走冷却）。
    /// 失败返回 false，由调用方走 Unconfirmed 路径。
    /// </summary>
    public async Task<bool> ResendBatchAsync(
        BatchTracker tracker, long newVerifierId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;

        var newVerifier = _verifierRepo.GetById(newVerifierId);
        if (newVerifier is null)
        {
            Log($"[verify-resend] batch={tracker.BatchId}：verifier id={newVerifierId} 不存在，放弃重发");
            return false;
        }

        var sender = _senderRepo.GetById(tracker.SenderId);
        if (sender is null)
        {
            Log($"[verify-resend] batch={tracker.BatchId}：sender id={tracker.SenderId} 不存在，放弃重发");
            return false;
        }

        // 模板用 _currentTemplate（SendEngine RunAsync 已持久化、未清空）。
        var tpl = _currentTemplate;
        if (tpl is null)
        {
            Log($"[verify-resend] batch={tracker.BatchId}：模板为 null（RunAsync 未启动或已完成），放弃重发");
            return false;
        }

        // 复原原始收件人列表
        var recipients = _recipientRepo.GetByIds(tracker.RecipientIds);
        if (recipients.Count == 0)
        {
            Log($"[verify-resend] batch={tracker.BatchId}：原始收件人为空（可能已被手动 reset / 删除），放弃重发");
            return false;
        }

        // 用新 verifier BCC 重发整批(走 executor 的简化路径:不写库、不计入配额、不触发事件)
        var result = await _executor.SendResendAsync(sender, tpl, recipients, newVerifier.Email, ct);
        if (!result.IsSuccess)
        {
            Log($"[verify-resend] batch={tracker.BatchId}：SMTP 失败 {result.ErrorMessage}，放弃重发走 Unconfirmed");
            _uiLogger?.Emit(LogLevel.Warning,
                $"换 verifier 重发失败 (…{BatchIdFormatter.Short(tracker.BatchId)})，分类 {result.Category}",
                account: sender.Email);
            return false;
        }

        // 关键：不调 IncrementSentToday；不调 RecordChargedQuota（保持 ChargedQuotaCount 不变）
        // 关键：不触发 BatchCompleted 事件（避免 UI 监控面板"等待验证中"被两条 SendBatchReport 同时展示）
        tracker.RecordAttemptedVerifier(newVerifierId);
        tracker.IncrementVerifierRetry();
        tracker.RecordResentAt(DateTime.UtcNow);

        _uiLogger?.Emit(LogLevel.Info,
            $"换 verifier 重发 (…{BatchIdFormatter.Short(tracker.BatchId)})，新验证邮箱 {newVerifier.Email}");

        return true;
    }

    /// <summary>跨天时把当天账号的 sent_today 归零、连续失败清零。</summary>
    private async Task ResetQuotaIfNeededAsync(DateTime nowLocal)
    {
        await Task.CompletedTask;
        var today = QuotaReset.Today(nowLocal);
        foreach (var s in _senderRepo.GetAll())
        {
            if (QuotaReset.NeedsReset(s.QuotaResetDate, nowLocal))
            {
                _senderRepo.UpdateStatus(s.Id, s.Status, consecutiveFailures: 0,
                    cooldownUntil: string.IsNullOrEmpty(s.CooldownUntil)
                        ? null : DateTime.Parse(s.CooldownUntil));
                _senderRepo.ResetSentToday(s.Id, today);
            }
        }
    }

    /// <summary>
    /// 把单批失败的结果接到账号状态机：
    ///   1) Classifier 决定 (newStatus / cooldownUntil / consecutive)
    ///   2) Unknown 阈值兜底已由 Classifier 内部完成
    ///   3) 收件人重试上限检查(> MaxSendAttempts → MarkFailed)
    ///   4) 日志 + 状态栏反馈
    /// </summary>
    private void ApplyFailure(Sender sender, SendBatchReport report, AppConfig config, List<Recipient> batch)
    {
        var result = report.SmtpResult!;
        var cls = _classifier.Classify(sender, result, config);

        var errMsg = result.ErrorMessage ?? result.Category.ToString();
        report.FailedCount = batch.Count;

        Log($"[失败] {sender.Email} → {batch.Count} 封 BCC：{errMsg}");
        _uiLogger?.Emit(LogLevel.Error, $"→ {batch.Count} 封 BCC：{errMsg}", account: sender.Email);
        report.ErrorMessage = errMsg;

        // 按 category 写对应的中文日志(原 ApplySingleFailureCategory 里的 switch 已搬到 Classifier,
        // 这里只负责输出,语义信息由 Classifier 的 NewStatus/CooldownUntil 承载)。
        switch (result.Category)
        {
            case SmtpErrorCategory.AuthFailure:
                Log($"[封禁] {sender.Email}：认证失败 ({errMsg})");
                _uiLogger?.Emit(LogLevel.Warning, $"认证失败 ({errMsg})", account: sender.Email);
                break;
            case SmtpErrorCategory.SpamDetected:
                Log($"[冷却] {sender.Email}：被识别为垃圾邮件（{errMsg}）");
                _uiLogger?.Emit(LogLevel.Warning, $"被识别为垃圾邮件（{errMsg}）", account: sender.Email);
                break;
            case SmtpErrorCategory.TemporaryFailure:
                Log($"[冷却] {sender.Email}：服务端临时不可用（{errMsg}）");
                _uiLogger?.Emit(LogLevel.Warning, $"服务端临时不可用（{errMsg}）", account: sender.Email);
                break;
            case SmtpErrorCategory.NetworkError:
                Log($"[冷却] {sender.Email}：网络错误（{errMsg}）");
                _uiLogger?.Emit(LogLevel.Warning, $"网络错误（{errMsg}）", account: sender.Email);
                break;
            case SmtpErrorCategory.Unknown when cls.NewStatus == SenderStatus.Cooling:
                Log($"[冷却] {sender.Email}：连续失败 {cls.ConsecutiveFailures} 次");
                _uiLogger?.Emit(LogLevel.Warning, $"连续失败 {cls.ConsecutiveFailures} 次", account: sender.Email);
                break;
        }

        ExpireOverAttemptedRecipients(batch.Select(r => r.Id), errMsg, config.MaxSendAttempts);

        _senderRepo.UpdateStatus(sender.Id, cls.NewStatus, cls.ConsecutiveFailures, cls.CooldownUntil);
        report.SenderStatusAfter = cls.NewStatus;
    }

    private static async Task SleepUntil(DateTime target, CancellationToken ct)
    {
        while (target > DateTime.Now && !ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 等批次状态被 VerificationWorker 推进（最长 SendHoldOnUnverifiedSeconds 秒）。
    /// 实际停止条件由 worker 推进 batch.State；如果超时仍未推进，按当前状态继续。
    /// </summary>
    private async Task WaitForBatchAsync(BatchTracker batch, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_verifierConfig.SendHoldOnUnverifiedSeconds);
        while (batch.IsPending && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (TaskCanceledException) { return; }
        }
    }

    private void SetState(EngineState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(this, s);
    }

    private void Log(string line)
    {
        Logged?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {line}");
    }

    /// <summary>
    /// 阻塞分支入口调用：emit 一条 UI 日志 + 触发 NextBatchHintChanged。
    /// wait &lt;= TimeSpan.Zero 时 hint 置为"下一批就绪"（兜底分支，主循环里通常走不到）。
    /// reason: "间隔" / "冷却到期" / "次日重置" / "跨出窗口" / "等待验证"。
    /// detail 拼接为 "（{reason}，{detail}）"；detail 为 null 时省略括号内逗号部分。
    /// </summary>
    private void SetNextBatchHint(string reason, TimeSpan wait, string? detail = null)
    {
        string text;
        if (wait <= TimeSpan.Zero)
        {
            text = "下一批就绪";
        }
        else
        {
            // 显示绝对时刻（东八区本地时间）而非倒计时：
            //   - 阻塞分支的 hint 文本常驻监控面板，"10min 后" 滚动起来数字会变，看不出"何时"；
            //   - "下一批 19:42:07 发送" 是固定锚点，用户看一眼就知道几分钟后。
            //   - 显式转 Asia/Shanghai 而非 DateTime.Now.Add（依赖宿主 TZ），
            //     保证部署到 UTC 服务器上仍显示北京时间。
            var nextLocal = DisplayTimeZone.ToDisplay(DateTime.UtcNow.Add(wait));
            text = detail is null
                ? $"下一批 {nextLocal:HH:mm:ss} 发送（{reason}）"
                : $"下一批 {nextLocal:HH:mm:ss} 发送（{reason}，{detail}）";
        }
        _uiLogger?.Emit(LogLevel.Info, text);
        NextBatchHintChanged?.Invoke(this, text);
    }

    /// <summary>
    /// 把本批成功发送的每封收件人地址独立写一行到物理日志文件（含 batch 与发件账号）。
    /// 整批只开一次文件，避免 valid.Count 较大时反复 IO。
    /// UI 路径不受影响（不走 LogBus.Emit，仅 LogFileWriter.WriteBlock）。
    /// </summary>
    private static void WriteSentRecipientBlock(string senderEmail, string batchId, List<Recipient> valid)
    {
        if (valid.Count == 0) return;
        var sb = new System.Text.StringBuilder(valid.Count * 96);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var r in valid)
        {
            sb.Append('[').Append(stamp).Append("] [SENT] ")
              .Append(r.Email)
              .Append(" | batch=").Append(batchId)
              .Append(" | sender=").Append(senderEmail)
              .Append('\n');
        }
        LogFileWriter.WriteBlock(sb.ToString());
    }

    /// <summary>
    /// 失败后处理：递增每个收件人的 attempt_count，读取新值；达到 maxAttempts 的
    /// 单独 MarkFailed 出队，未达到的保留 pending 等待下一轮重试。
    /// </summary>
    private void ExpireOverAttemptedRecipients(IEnumerable<long> recipientIds, string errorMessage, int maxAttempts)
    {
        var ids = recipientIds.ToList();
        if (ids.Count == 0 || maxAttempts < 1) return;

        var newCounts = _recipientRepo.IncrementAttemptsAndReturnCounts(ids);
        var expired = new List<long>();
        foreach (var (id, count) in newCounts)
        {
            if (count >= maxAttempts) expired.Add(id);
        }
        foreach (var id in expired)
        {
            _recipientRepo.MarkFailed(id, $"重试 {maxAttempts} 次仍失败：{errorMessage}");
            Log($"[失败-出队] recipient_id={id} 已达 {maxAttempts} 次尝试上限，标记为 failed");
            _uiLogger?.Emit(LogLevel.Error, $"recipient_id={id} 已达 {maxAttempts} 次尝试上限");
        }
    }
}