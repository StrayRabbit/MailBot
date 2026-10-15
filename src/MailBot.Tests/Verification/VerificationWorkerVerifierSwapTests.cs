using MailBot.Core.Models;
using MailBot.Core.Senders;
using MailBot.Core.Sending;
using MailBot.Core.Storage;
using MailBot.Core.Verification;
using Xunit;

namespace MailBot.Tests.Verification;

/// <summary>
/// 测试 VerificationWorker 在 IMAP 未命中时的"换 verifier 重发"分支。
/// 走 FakeResendHandler 替代真实 SMTP 栈，直接控制重发成功/失败。
/// </summary>
public class VerificationWorkerVerifierSwapTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly VerifierRepository _verifierRepo;
    private readonly RecipientRepository _recipientRepo;
    private readonly SenderRepository _senderRepo;
    private readonly SenderPool _senderPool;
    private readonly List<string> _logs = new();

    public VerificationWorkerVerifierSwapTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_swap_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _verifierRepo = new VerifierRepository(_db);
        _recipientRepo = new RecipientRepository(_db);
        _senderRepo = new SenderRepository(_db);
        _senderPool = new SenderPool(_senderRepo);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    /// <summary>FakeResendHandler：可注入委托决定成功/失败，并跟踪调用历史。</summary>
    private sealed class FakeResendHandler : IResendHandler
    {
        public List<(BatchTracker tracker, long verifierId, CancellationToken ct)> Calls { get; } = new();
        public Func<BatchTracker, long, Task<bool>>? Impl { get; set; }

        public Task<bool> ResendBatchAsync(BatchTracker tracker, long newVerifierId, CancellationToken ct)
        {
            Calls.Add((tracker, newVerifierId, ct));
            return Impl?.Invoke(tracker, newVerifierId) ?? Task.FromResult(false);
        }
    }

    /// <summary>按 verifier.Id 路由返回内容的 IMAP mock。</summary>
    private sealed class VerifierRoutedImap : IImapPoller
    {
        private readonly Dictionary<long, (string, uint, DateTimeOffset?)[]> _byVerifierId;
        public VerifierRoutedImap(Dictionary<long, (string, uint, DateTimeOffset?)[]> byVerifierId)
            => _byVerifierId = byVerifierId;
        public Task<IReadOnlyList<(string FromEmail, uint Uid, DateTimeOffset? InternalDate)>>
            PollSinceAsync(VerifierAccount account, DateTimeOffset r, CancellationToken ct)
        {
            var list = _byVerifierId.TryGetValue(account.Id, out var items)
                ? items
                : Array.Empty<(string, uint, DateTimeOffset?)>();
            return Task.FromResult<IReadOnlyList<(string, uint, DateTimeOffset?)>>(list);
        }
        public Task<(bool ok, string? error)> TestConnectionAsync(VerifierAccount account, CancellationToken ct)
            => Task.FromResult((true, (string?)null));
        public Task<uint?> GetInboxMaxUidAsync(VerifierAccount account, CancellationToken ct)
            => Task.FromResult<uint?>(null);
    }

    private long InsertSender(string email, int dailyQuota = 50)
    {
        return _senderRepo.Insert(new Sender
        {
            Email = email, SmtpHost = "h", SmtpPort = 465, Username = "u",
            PasswordEncrypted = Array.Empty<byte>(),
            DailyQuota = dailyQuota,
            QuotaResetDate = DateTime.Now.ToString("yyyy-MM-dd"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    private long InsertVerifier(string email)
    {
        return _verifierRepo.Insert(new VerifierAccount
        {
            Email = email, ImapHost = "i", ImapPort = 993, Username = email,
            PasswordEncrypted = Array.Empty<byte>(), Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private Task InvokePollAsync(VerificationWorker worker)
    {
        var m = typeof(VerificationWorker).GetMethod("PollOnceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (Task)m!.Invoke(worker, new object?[] { CancellationToken.None, false })!;
    }

    /// <summary>基础 helper：建 worker、注册 batch（已 pre-populate AttemptedVerifierIds=[initialVerifierId]）。</summary>
    private (VerificationWorker worker, FakeResendHandler resend, BatchTracker batch)
        BuildAndRegister(long senderId, long initialVerifierId, IReadOnlyList<long> recipientIds,
            string batchId, VerifierConfig config, VerifierRoutedImap imap)
    {
        var resend = new FakeResendHandler();
        var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            config, imap, _logs.Add, resendHandler: resend);
        var batch = new BatchTracker(batchId, DateTime.UtcNow.AddSeconds(-200),
            senderId, recipientIds, initialVerifierId: initialVerifierId);
        worker.RegisterBatch(batch);
        return (worker, resend, batch);
    }

    [Fact]
    public async Task Swap_Succeeds_ResetsCheckAttempts_AndUsesNewVerifier()
    {
        var sid = InsertSender("sender@example.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");

        // SentAt 200s 前 = 已过 RecheckIntervalSeconds=180
        // batch 首发已选 v1；IMAP 让 v1/v2 都未命中 → 触发 swap
        var imap = new VerifierRoutedImap(new Dictionary<long, (string, uint, DateTimeOffset?)[]>
        {
            [v1] = Array.Empty<(string, uint, DateTimeOffset?)>(),
            [v2] = Array.Empty<(string, uint, DateTimeOffset?)>(),
        });
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 1, MaxVerifierAttemptsPerBatch = 2 };
        var (worker, resend, batch) = BuildAndRegister(sid, v1, new long[] { 1L }, "swap-1", cfg, imap);

        resend.Impl = (t, vid) =>
        {
            // 模拟 SendEngine.ResendBatchAsync 的副作用
            t.RecordAttemptedVerifier(vid);
            t.IncrementVerifierRetry();
            t.RecordResentAt(DateTime.UtcNow);
            return Task.FromResult(true);
        };

        await InvokePollAsync(worker);

        Assert.Equal(1, resend.Calls.Count);  // 重发被调 1 次
        Assert.Equal(v2, resend.Calls[0].verifierId);  // 选下一个 = v2（v1 已在 AttemptedVerifierIds）
        Assert.Equal(2, batch.AttemptedVerifierIds.Count);  // [v1, v2]
        Assert.Equal(1, batch.VerifierRetryCount);
        Assert.NotNull(batch.ResentAt);
        Assert.Equal(BatchState.Pending, batch.State);  // 重发后未命中分支，状态仍 Pending
        Assert.Equal(0, batch.CheckAttempts);  // ResetCheckAttemptsForResend 已触发
    }

    [Fact]
    public async Task Swap_FailsSmtp_GoesToUnconfirmed()
    {
        var sid = InsertSender("a@x.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");

        var imap = new VerifierRoutedImap(new Dictionary<long, (string, uint, DateTimeOffset?)[]>
        {
            [v1] = Array.Empty<(string, uint, DateTimeOffset?)>(),
            [v2] = Array.Empty<(string, uint, DateTimeOffset?)>(),
        });
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 1, MaxVerifierAttemptsPerBatch = 2 };
        var (worker, resend, batch) = BuildAndRegister(sid, v1, new long[] { 1L }, "swap-fail", cfg, imap);

        resend.Impl = (_, _) => Task.FromResult(false);  // SMTP 失败

        await InvokePollAsync(worker);

        Assert.Equal(BatchState.Unconfirmed, batch.State);
        Assert.Equal(1, batch.UnconfirmedAttempts);
        Assert.Single(batch.AttemptedVerifierIds);  // SMTP 失败时没追加 v2
    }

    [Fact]
    public async Task BothVerifiersMissed_GoesToUnconfirmed_NoRefundDouble()
    {
        // 场景：批次已首发 v1、未命中；现在 v2 也被列入 AttemptedVerifierIds（即已 swap v2）。
        // 配置 MaxVerifierAttemptsPerBatch=2（首发 v1 占 1；swap v2 占 2 = 触顶）
        // + MaxPollAttempts=1 + candidatesForBatch 仍有候选（v3）→ 走 IMAP 查询
        // 未命中后 canStillSwap=false + CheckAttempts≥MaxPollAttempts → Unconfirmed。
        var sid = InsertSender("a@x.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");
        var v3 = InsertVerifier("v3@y.com");

        var imap = new VerifierRoutedImap(new Dictionary<long, (string, uint, DateTimeOffset?)[]>
        {
            [v1] = Array.Empty<(string, uint, DateTimeOffset?)>(),
            [v2] = Array.Empty<(string, uint, DateTimeOffset?)>(),
            [v3] = Array.Empty<(string, uint, DateTimeOffset?)>(),
        });
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 1, MaxVerifierAttemptsPerBatch = 2 };

        var resend = new FakeResendHandler();
        var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            cfg, imap, _logs.Add, resendHandler: resend);
        // 手工构造"已首发 v1 + 已 swap v2"的 batch
        var batch = new BatchTracker("swap-both-miss", DateTime.UtcNow.AddSeconds(-200),
            sid, new long[] { 1L }, initialVerifierId: v1);
        batch.RecordAttemptedVerifier(v2);
        batch.IncrementVerifierRetry();
        batch.RecordResentAt(DateTime.UtcNow.AddSeconds(-200));
        batch.RecordChargedQuota(3);  // 模拟首发已扣 3 封配额
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        // canStillSwap=false (2 < 2 == false) + CheckAttempts(1) ≥ MaxPollAttempts(1) → Unconfirmed
        Assert.Equal(BatchState.Unconfirmed, batch.State);
    }

    [Fact]
    public async Task OnlyOneVerifierEnabled_GoesToUnconfirmed()
    {
        // 场景：只配 1 个 verifier（首发时它就是唯一候选项）。
        // 配置 MaxVerifierAttemptsPerBatch=2 但 enabled=1 → swap 时没有新 verifier 可选
        // + candidatesForBatch=[v1]（没达 MaxVerifierAttemptsPerBatch 上限）→ 查 v1 → 未命中
        // → canStillSwap=true（Count=1 < 2）但 PickNextVerifier 返回 null → 走 Unconfirmed
        var sid = InsertSender("a@x.com");
        var v1 = InsertVerifier("v1@y.com");

        var imap = new VerifierRoutedImap(new Dictionary<long, (string, uint, DateTimeOffset?)[]>
        {
            [v1] = Array.Empty<(string, uint, DateTimeOffset?)>(),
        });
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 1, MaxVerifierAttemptsPerBatch = 2 };

        var resend = new FakeResendHandler();
        var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            cfg, imap, _logs.Add, resendHandler: resend);
        var batch = new BatchTracker("swap-one-only", DateTime.UtcNow.AddSeconds(-200),
            sid, new long[] { 1L }, initialVerifierId: v1);
        batch.RecordChargedQuota(1);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        Assert.Equal(BatchState.Unconfirmed, batch.State);
        Assert.Empty(resend.Calls);  // 没真正调 resend（PickNextVerifier 返回 null）
    }

    [Fact]
    public async Task SwapAttempt_ButNoEnabledLeft_GoesToUnconfirmed()
    {
        // 模拟：首发时 enabled=[v1, v2]，但下一次轮询时 v2 被禁用（ListEnabled 返 [v1]）。
        // 首发 v1 占用，但 v1 仍可能被查：candidatesForBatch=[v1]（未达 Max 上限）→ 查 v1 未命中
        // → canStillSwap=true（Count=1<2）→ PickNextVerifier 排除 v1 后 [v2] 已被禁用 → 返 null → 走 Unconfirmed
        var sid = InsertSender("a@x.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");

        var imap = new VerifierRoutedImap(new Dictionary<long, (string, uint, DateTimeOffset?)[]>
        {
            [v1] = Array.Empty<(string, uint, DateTimeOffset?)>(),
            [v2] = Array.Empty<(string, uint, DateTimeOffset?)>(),
        });
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 1, MaxVerifierAttemptsPerBatch = 2 };

        var resend = new FakeResendHandler();
        var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            cfg, imap, _logs.Add, resendHandler: resend);
        var batch = new BatchTracker("swap-disabled", DateTime.UtcNow.AddSeconds(-200),
            sid, new long[] { 1L }, initialVerifierId: v1);
        batch.RecordChargedQuota(1);
        worker.RegisterBatch(batch);

        // 第 1 次轮询前禁用 v2
        _verifierRepo.Update(new VerifierAccount
        {
            Id = v2, Email = "v2@y.com", ImapHost = "i", ImapPort = 993, Username = "v2",
            PasswordEncrypted = Array.Empty<byte>(), Enabled = false,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        await InvokePollAsync(worker);

        // v1 未命中 + 没 verifier 可 swap → Unconfirmed
        Assert.Equal(BatchState.Unconfirmed, batch.State);
        Assert.Empty(resend.Calls);
    }

    [Fact]
    public async Task Swap_HonorsResentAt_NotOriginalSentAt()
    {
        // 首发 SentAt 设较老，swap 后 ResentAt 设为现在。验证：
        // 1) PollOnceAsync 不再因为"原 SentAt 老"误触发兜底 Unconfirmed
        // 2) tracker.ResentAt 被正确写入
        var sid = InsertSender("a@x.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");

        var imap = new VerifierRoutedImap(new Dictionary<long, (string, uint, DateTimeOffset?)[]>
        {
            [v1] = Array.Empty<(string, uint, DateTimeOffset?)>(),
            [v2] = Array.Empty<(string, uint, DateTimeOffset?)>(),
        });
        var cfg = new VerifierConfig
        {
            RecheckIntervalSeconds = 180,
            MaxPollAttempts = 1,
            MaxVerifierAttemptsPerBatch = 2,
            LookbackMinutes = 5,
        };
        var (worker, resend, batch) = BuildAndRegister(sid, v1, new long[] { 1L }, "swap-resentat", cfg, imap);

        // SentAt = 200s 前（在 RecheckIntervalSeconds=180 与 LookbackMinutes=5min 范围内）
        resend.Impl = (t, vid) =>
        {
            t.RecordAttemptedVerifier(vid);
            t.IncrementVerifierRetry();
            t.RecordResentAt(DateTime.UtcNow);
            return Task.FromResult(true);
        };

        var sentAtBefore = batch.SentAt;
        await InvokePollAsync(worker);

        // swap 成功后 ResentAt 被设，SentAt 不变
        Assert.NotNull(batch.ResentAt);
        Assert.Equal(sentAtBefore, batch.SentAt);
        Assert.True(batch.ResentAt >= sentAtBefore);
        Assert.Equal(BatchState.Pending, batch.State);
        Assert.Single(resend.Calls);
    }

    [Fact]
    public async Task ColdStart_RestoresBatch_WithEmptyAttemptedVerifiers()
    {
        var sid = InsertSender("a@x.com");
        var v1 = InsertVerifier("v1@y.com");

        _recipientRepo.InsertBatch(new[] { new Recipient { Email = "r1@x.com" } }, sourceFile: null);
        var rid = _recipientRepo.GetAll()[0].Id;
        _recipientRepo.MarkUnverified(new[] { rid }, "cold-swap");

        var imap = new VerifierRoutedImap(new Dictionary<long, (string, uint, DateTimeOffset?)[]>
        {
            [v1] = Array.Empty<(string, uint, DateTimeOffset?)>(),
        });
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 1, MaxVerifierAttemptsPerBatch = 2 };
        var resend = new FakeResendHandler();
        var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            cfg, imap, _logs.Add, resendHandler: resend);
        await worker.StartAsync(CancellationToken.None);

        // 等待 ColdStart 完成（批次注册完成）。
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (worker.ActiveBatches.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        var batch = worker.ActiveBatches.Single();
        // 冷启动批次不传 initialVerifierId → AttemptedVerifierIds 为空
        Assert.Empty(batch.AttemptedVerifierIds);

        worker.Stop();
    }

    [Fact]
    public async Task Swap_CandidateExcludesPreviouslyAttemptedVerifiers_WhenMaxReached()
    {
        // 验：当 AttemptedVerifierIds 已达 MaxVerifierAttemptsPerBatch 上限时，
        // 候选过滤排除已用过的 verifier；这是"无新 verifier 可扩选"的边界场景。
        var sid = InsertSender("a@x.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");
        var v3 = InsertVerifier("v3@y.com");

        var imap = new VerifierRoutedImap(new Dictionary<long, (string, uint, DateTimeOffset?)[]>
        {
            [v1] = Array.Empty<(string, uint, DateTimeOffset?)>(),
            [v2] = Array.Empty<(string, uint, DateTimeOffset?)>(),
            [v3] = Array.Empty<(string, uint, DateTimeOffset?)>(),
        });
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 1, MaxVerifierAttemptsPerBatch = 2 };
        var resend = new FakeResendHandler();
        var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            cfg, imap, _logs.Add, resendHandler: resend);

        // 手工构造已 swap v2 的 batch（首发 v1 + swap v2 已用）
        var batch = new BatchTracker("swap-candidate", DateTime.UtcNow.AddSeconds(-200),
            sid, new long[] { 1L }, initialVerifierId: v1);
        batch.RecordAttemptedVerifier(v2);
        batch.IncrementVerifierRetry();
        batch.RecordResentAt(DateTime.UtcNow.AddSeconds(-200));
        batch.RecordChargedQuota(1);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        // 达 MaxVerifierAttemptsPerBatch 上限：candidatesForBatch = enabled - [v1, v2] = [v3]
        // v3 未命中 + canStillSwap=false → 走 Unconfirmed
        Assert.Equal(BatchState.Unconfirmed, batch.State);
    }
}