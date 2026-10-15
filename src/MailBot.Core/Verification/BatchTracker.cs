namespace MailBot.Core.Verification;

/// <summary>
/// 单个批次的送达验证状态机。in-memory，由 VerificationWorker 统一注册与推进。
/// 终态后所有 setter 静默忽略（不抛），方便 worker 多次轮询不会重复触发事件。
/// </summary>
public sealed class BatchTracker
{
    public string BatchId { get; }
    public DateTime SentAt { get; }
    public long SenderId { get; }
    public BatchState State { get; private set; } = BatchState.Pending;
    public IReadOnlyList<long> RecipientIds { get; }

    public DateTime? ResolvedAt { get; private set; }
    public string? Resolution { get; private set; }

    /// <summary>
    /// VerificationWorker 对该批次实际发起 IMAP 检查的次数。
    /// 0 = 还在等首查间隔；1 = 已首查过；2 = 重查后仍未命中 → 触发 MarkUnconfirmed。
    /// 终态后不再递增（避免事件被多次触发）。
    /// </summary>
    public int CheckAttempts { get; private set; }

    /// <summary>
    /// 该批次已经 unconfirmed 过的次数。决定下次 unconfirmed 时的冷却翻倍基数。
    /// 重启时由 VerificationWorker 从 DB 读 max(recipients.unconfirmed_count) 重建。
    /// </summary>
    public int UnconfirmedAttempts { get; private set; }

    /// <summary>
    /// 本批已实际计入 senders.sent_today 的邮件个数。由 SendEngine 在 IncrementSentToday 时写入。
    /// VerificationWorker 在 IMAP 超时未命中时按本字段退额——避免退得比扣得多
    /// （RecipientIds.Count 是原始入站总数，可能 > 实际扣减，因为 PartitionValid 会过滤掉无效收件人）。
    /// </summary>
    public int ChargedQuotaCount { get; private set; }

    /// <summary>
    /// 本批已使用过的 verifier id 列表（含首发）。Pending 时追加；终态后 setter 静默忽略。
    /// 用于"换 verifier 重发"逻辑：下一轮轮询跳过这些 verifier，候选 verifier 数已达上限则走 Unconfirmed。
    /// </summary>
    private readonly List<long> _attemptedVerifierIds = new();
    public IReadOnlyList<long> AttemptedVerifierIds => _attemptedVerifierIds;

    /// <summary>
    /// 已换 verifier 的次数（首发占 1 个 verifier 但 VerifierRetryCount=0；首次 swap 后 =1）。
    /// 用于 UI 日志展示"已 swap 几次"；控制流判定仍以 AttemptedVerifierIds.Count 为准。
    /// </summary>
    public int VerifierRetryCount { get; private set; }

    /// <summary>
    /// 上次重发时刻（UTC）。重发整批后由 SendEngine.ResendBatchAsync 写入；worker 用 ResentAt ?? SentAt 算 elapsed，
    /// 避免老 SentAt 跨过 LookbackMinutes 兜底直接 Unconfirmed。
    /// </summary>
    public DateTime? ResentAt { get; private set; }

    public BatchTracker(
        string batchId,
        DateTime sentAt,
        long senderId,
        IReadOnlyList<long> recipientIds,
        int initialUnconfirmedAttempts = 0,
        long? initialVerifierId = null)
    {
        BatchId = batchId;
        SentAt = sentAt;
        SenderId = senderId;
        RecipientIds = recipientIds;
        for (int i = 0; i < initialUnconfirmedAttempts; i++)
            IncrementUnconfirmedAttempts();
        if (initialVerifierId is long vid)
        {
            RecordAttemptedVerifier(vid);
        }
    }

    public void MarkVerified()
    {
        if (State != BatchState.Pending) return;
        State = BatchState.Verified;
        ResolvedAt = DateTime.UtcNow;
        Resolution = "verified-by-imap";
    }

    public void MarkUnconfirmed()
    {
        if (State != BatchState.Pending) return;
        State = BatchState.Unconfirmed;
        ResolvedAt = DateTime.UtcNow;
        Resolution = "unconfirmed-timeout";
    }

    /// <summary>仅在仍 Pending 时递增检查次数。终态调用静默忽略。</summary>
    public void IncrementCheckAttempts()
    {
        if (State != BatchState.Pending) return;
        CheckAttempts++;
    }

    /// <summary>仅在仍 Pending 时累加 unconfirmed 次数。终态调用静默忽略。</summary>
    public void IncrementUnconfirmedAttempts()
    {
        if (State != BatchState.Pending) return;
        UnconfirmedAttempts++;
    }

    /// <summary>
    /// 记录本批已计入的配额数。仅在仍 Pending 时生效（终态后调用静默忽略），
    /// 避免 IMAP 命中/超时分支对已记账的批次再次覆写。
    /// </summary>
    public void RecordChargedQuota(int count)
    {
        if (State != BatchState.Pending) return;
        if (count <= 0) return;
        ChargedQuotaCount = count;
    }

    /// <summary>记录本批已使用过的 verifier。Pending 时追加；同 id 重复调用幂等。终态后静默忽略。</summary>
    public void RecordAttemptedVerifier(long verifierId)
    {
        if (State != BatchState.Pending) return;
        if (_attemptedVerifierIds.Contains(verifierId)) return;
        _attemptedVerifierIds.Add(verifierId);
    }

    /// <summary>递增"换 verifier"重发次数。Pending 才生效；终态后静默忽略。</summary>
    public void IncrementVerifierRetry()
    {
        if (State != BatchState.Pending) return;
        VerifierRetryCount++;
    }

    /// <summary>记录重发时刻（用于替换轮询窗口的起点）。Pending 才生效；终态后静默忽略。</summary>
    public void RecordResentAt(DateTime atUtc)
    {
        if (State != BatchState.Pending) return;
        ResentAt = atUtc;
    }

    /// <summary>
    /// 重发整批后把 CheckAttempts 重置为 0，让下一轮轮询重新走 MaxPollAttempts 次节奏。
    /// 仅 Pending 生效；终态后静默忽略。
    /// </summary>
    public void ResetCheckAttemptsForResend()
    {
        if (State != BatchState.Pending) return;
        CheckAttempts = 0;
    }

    public bool IsPending => State == BatchState.Pending;
}