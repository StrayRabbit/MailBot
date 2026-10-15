using MailBot.Core.Models;
using MailBot.Core.Senders;
using MailBot.Core.Sending;
using MailBot.Core.Storage;
using MailBot.Core.Verification;
using Xunit;

namespace MailBot.Tests.Verification;

public class VerificationWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly VerifierRepository _verifierRepo;
    private readonly RecipientRepository _recipientRepo;
    private readonly SenderRepository _senderRepo;
    private readonly SenderPool _senderPool;
    private readonly List<string> _logs = new();

    public VerificationWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_test_{Guid.NewGuid():N}.db");
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

    /// <summary>Mock IMAP poller：返回预设发件账号列表，可附带 internalDate。</summary>
    private sealed class FakeImapPoller : IImapPoller
    {
        private readonly List<(string FromEmail, uint Uid, DateTimeOffset? InternalDate)> _items;
        public FakeImapPoller(
            IEnumerable<(string fromEmail, uint uid, DateTimeOffset? internalDate)> items)
            => _items = items.ToList();
        /// <summary>如果设置，会按 receivedSince 真正过滤（模拟真实 IMAP 行为）。</summary>
        public bool HonorReceivedSince { get; init; }
        public Task<IReadOnlyList<(string FromEmail, uint Uid, DateTimeOffset? InternalDate)>>
            PollSinceAsync(
                VerifierAccount account, DateTimeOffset receivedSince, CancellationToken ct)
        {
            var list = HonorReceivedSince
                ? _items.Where(s => s.InternalDate is null || s.InternalDate >= receivedSince).ToList()
                : _items;
            return Task.FromResult<IReadOnlyList<(string, uint, DateTimeOffset?)>>(list);
        }
        public Task<(bool ok, string? error)> TestConnectionAsync(
            VerifierAccount account, CancellationToken ct) => Task.FromResult((true, (string?)null));
        public Task<uint?> GetInboxMaxUidAsync(
            VerifierAccount account, CancellationToken ct)
            => Task.FromResult<uint?>(null);
    }

    private long InsertSender(string email, int dailyQuota = 50)
    {
        return _senderRepo.Insert(new Sender
        {
            Email = email, SmtpHost = "h", SmtpPort = 465, Username = "u",
            PasswordEncrypted = Array.Empty<byte>(),
            DailyQuota = dailyQuota,
            QuotaResetDate = "2026-09-25",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    private long InsertVerifier(string email)
    {
        return _verifierRepo.Insert(new VerifierAccount
        {
            Email = email, ImapHost = "i", ImapPort = 993, Username = email,
            PasswordEncrypted = Array.Empty<byte>(), Enabled = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>反射调用 PollOnceAsync（private）。</summary>
    private Task InvokePollAsync(VerificationWorker worker)
    {
        var m = typeof(VerificationWorker).GetMethod("PollOnceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (Task)m!.Invoke(worker, new object?[] { CancellationToken.None, false })!;
    }

    [Fact]
    public void RegisterBatch_AddsToActive()
    {
        var imap = new FakeImapPoller(Array.Empty<(string, uint, DateTimeOffset?)>());
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool, new VerifierConfig(), imap, _logs.Add);
        var batch = new BatchTracker("2026-09-25-001-aaaa", DateTime.UtcNow, 1, new long[] { 1, 2 });
        worker.RegisterBatch(batch);
        Assert.Single(worker.ActiveBatches);
    }

    [Fact]
    public async Task Confirm_OnFirstCheck_WhenSenderEmailInInbox()
    {
        var senderId = InsertSender("a@x.com");
        InsertVerifier("v@y.com");
        // SentAt 设为 200s 前，已超过 RecheckIntervalSeconds=180s → 允许 verified
        var batch = new BatchTracker("batch-001", DateTime.UtcNow.AddSeconds(-200), senderId, new long[] { 1 });
        // IMAP 命中条件：verifier 收到 sender.Email 的邮件
        var imap = new FakeImapPoller(new[] { ("a@x.com", (uint)10, (DateTimeOffset?)DateTimeOffset.UtcNow) });
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool, new VerifierConfig { RecheckIntervalSeconds = 180 }, imap, _logs.Add);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        Assert.Equal(BatchState.Verified, batch.State);
        Assert.Equal(1, batch.CheckAttempts);
        // 文件日志不再输出 [verified]（计划 Task 2 已精简）；UI 日志通过 LogBus.Emit 仍输出
    }

    [Fact]
    public async Task FirstCheckMissed_IncrementsCheckAttempts_StaysPending()
    {
        var senderId = InsertSender("a@x.com");
        InsertVerifier("v@y.com");
        var batch = new BatchTracker("batch-002", DateTime.UtcNow.AddSeconds(-200), senderId, new long[] { 1 });
        // IMAP 拉到的邮件来自其他发件人 → 不命中
        var imap = new FakeImapPoller(new[] { ("other@x.com", (uint)5, (DateTimeOffset?)DateTimeOffset.UtcNow) });
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig
            {
                RecheckIntervalSeconds = 180,
                MaxPollAttempts = 2,
            }, imap, _logs.Add);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        Assert.Equal(BatchState.Pending, batch.State);
        Assert.Equal(1, batch.CheckAttempts);
        // 文件日志不再输出 [check-miss]（计划 Task 2 已精简）；UI 日志通过 LogBus.Emit 仍输出
    }

    [Fact]
    public async Task SecondCheckMissed_TriggersUnconfirmed_AndResetsBatchToPending()
    {
        var senderId = InsertSender("coolme@x.com");
        InsertVerifier("v@y.com");

        _recipientRepo.InsertBatch(new[]
        {
            new Recipient { Email = "r1@x.com" },
            new Recipient { Email = "r2@x.com" },
        }, sourceFile: null);
        var all = _recipientRepo.GetByStatus("pending", 10);
        Assert.Equal(2, all.Count);
        _recipientRepo.MarkUnverified(all.Select(r => r.Id), "batch-003");

        // SentAt 设 200s 前：过 RecheckIntervalSeconds=180 但 < LookbackMinutes=5min=300s
        var batch = new BatchTracker("batch-003", DateTime.UtcNow.AddSeconds(-200), senderId, all.Select(r => r.Id).ToList());
        // 合并发送后每个 batch 固定 1 个 verifier → MaxPollAttempts=2 时需两次未命中才 Unconfirmed

        var imap = new FakeImapPoller(Array.Empty<(string, uint, DateTimeOffset?)>());
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig
            {
                RecheckIntervalSeconds = 180,
                MaxPollAttempts = 2,
                LookbackMinutes = 5,
                UnconfirmedCooldownMinutes = 60,
            }, imap, _logs.Add);
        worker.RegisterBatch(batch);

        // 第 1 次轮询：未命中 → CheckAttempts=1，仍 Pending
        await InvokePollAsync(worker);
        Assert.Equal(BatchState.Pending, batch.State);
        Assert.Equal(1, batch.CheckAttempts);

        // 第 2 次轮询：仍未命中 → CheckAttempts=2 ≥ MaxPollAttempts → Unconfirmed
        await InvokePollAsync(worker);
        Assert.Equal(BatchState.Unconfirmed, batch.State);
        Assert.Equal(1, batch.UnconfirmedAttempts);

        var pending = _recipientRepo.GetByStatus("pending", 10);
        Assert.Equal(2, pending.Count);
        var sender = _senderRepo.GetById(senderId);
        Assert.Equal(SenderStatus.Cooling, sender!.Status);
        Assert.NotNull(sender.CooldownUntil);
        // 文件日志不再输出 [unconfirmed]（计划 Task 2 已精简）；UI 日志通过 LogBus.Emit 仍输出
    }

    [Fact]
    public async Task SentAtAnchor_EmailBeforeSentAt_NotReturned()
    {
        // IMAP SentAt 起点：SearchQuery.DeliveredAfter(batch.SentAt) 不返回 SentAt 之前的邮件
        var senderId = InsertSender("a@x.com");
        InsertVerifier("v@y.com");
        var sentAt = DateTime.UtcNow.AddSeconds(-200);
        var batch = new BatchTracker("batch-100", sentAt, senderId, new long[] { 1 });
        // 邮件 Received 比 SentAt 早 30 分钟 → 严格按 SentAt 起点应被过滤
        var earlyReceived = DateTimeOffset.UtcNow.AddHours(-12);
        var imap = new FakeImapPoller(new[]
        {
            ("a@x.com", (uint)10, (DateTimeOffset?)earlyReceived),
        }) { HonorReceivedSince = true };
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig
            {
                RecheckIntervalSeconds = 180,
                MaxPollAttempts = 2,
            }, imap, _logs.Add);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        // IMAP 没返回 SentAt 之前的邮件 → 未命中 → CheckAttempts = 1（Pending）
        Assert.Equal(BatchState.Pending, batch.State);
        Assert.Equal(1, batch.CheckAttempts);
        // 文件日志不再输出 [verified]/[unconfirmed]（计划 Task 2 已精简）；状态机断言已覆盖语义
    }

    [Fact]
    public async Task SentAtAnchor_EmailAfterSentAt_Counted()
    {
        // 即使邮件 Received 比 SentAt 早一点（跨时区），只要 DeliveredAfter(SentAt) 能查到，就命中
        var senderId = InsertSender("a@x.com");
        InsertVerifier("v@y.com");
        var sentAt = DateTime.UtcNow.AddSeconds(-200);
        var batch = new BatchTracker("batch-cross-tz", sentAt, senderId, new long[] { 1 });
        // 把 mock 实现改为：返回 SentAt 之后的"邮件"（HonorReceivedSince=true 自动按 receivedSince 过滤）
        var internalDate = new DateTimeOffset(sentAt.AddSeconds(30), TimeSpan.Zero);
        var imap = new FakeImapPoller(new[]
        {
            ("a@x.com", (uint)10, (DateTimeOffset?)internalDate),
        }) { HonorReceivedSince = true };
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig
            {
                RecheckIntervalSeconds = 180,
                MaxPollAttempts = 2,
            }, imap, _logs.Add);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        Assert.Equal(BatchState.Verified, batch.State);
    }

    [Fact]
    public async Task SentAtAnchor_EmailWithoutInternalDate_Counted()
    {
        // 部分 IMAP 服务端不返回 InternalDate，应放行（不误判）。
        var senderId = InsertSender("a@x.com");
        InsertVerifier("v@y.com");
        var batch = new BatchTracker("batch-no-date", DateTime.UtcNow.AddSeconds(-200), senderId, new long[] { 1 });
        var imap = new FakeImapPoller(new[]
        {
            ("a@x.com", (uint)10, (DateTimeOffset?)null),
        });
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 2 }, imap, _logs.Add);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        Assert.Equal(BatchState.Verified, batch.State);
    }

    [Fact]
    public async Task BeforeFirstCheckInterval_PendingBatchNotInspected()
    {
        var senderId = InsertSender("a@x.com");
        InsertVerifier("v@y.com");
        // SentAt 刚刚：远小于 RecheckIntervalSeconds=180s
        var batch = new BatchTracker("batch-200", DateTime.UtcNow.AddSeconds(-10), senderId, new long[] { 1 });
        var imap = new FakeImapPoller(new[]
        {
            ("a@x.com", (uint)10, (DateTimeOffset?)DateTimeOffset.UtcNow),
        });
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 2 }, imap, _logs.Add);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        Assert.Equal(BatchState.Pending, batch.State);
        Assert.Equal(0, batch.CheckAttempts);
    }

    [Fact]
    public async Task MaxPollAttempts2_TriggersUnconfirmedAfterTwoMisses()
    {
        var senderId = InsertSender("triple@x.com");
        InsertVerifier("v@y.com");
        _recipientRepo.InsertBatch(new[]
        {
            new Recipient { Email = "r1@x.com" },
        }, sourceFile: null);
        var all = _recipientRepo.GetByStatus("pending", 10);
        _recipientRepo.MarkUnverified(all.Select(r => r.Id), "batch-triple");

        // SentAt 设 200s 前：过 RecheckIntervalSeconds=180 但 < LookbackMinutes=5min=300s
        var batch = new BatchTracker("batch-triple", DateTime.UtcNow.AddSeconds(-200), senderId,
            all.Select(r => r.Id).ToList());
        // 合并发送后每个 batch 固定 1 个 verifier，MaxPollAttempts=2 → 2 次未命中即 Unconfirmed
        var imap = new FakeImapPoller(Array.Empty<(string, uint, DateTimeOffset?)>());
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig
            {
                RecheckIntervalSeconds = 180,
                MaxPollAttempts = 2,
                LookbackMinutes = 5,
                UnconfirmedCooldownMinutes = 60,
            }, imap, _logs.Add);
        worker.RegisterBatch(batch);

        // 第 1 次轮询：CheckAttempts=1，Pending
        await InvokePollAsync(worker);
        Assert.Equal(BatchState.Pending, batch.State);
        Assert.Equal(1, batch.CheckAttempts);

        // 第 2 次轮询：CheckAttempts=2 ≥ MaxPollAttempts=2 → Unconfirmed
        await InvokePollAsync(worker);
        Assert.Equal(BatchState.Unconfirmed, batch.State);
        Assert.Equal(1, batch.UnconfirmedAttempts);
        // 文件日志不再输出 [unconfirmed]（计划 Task 2 已精简）；UI 日志通过 LogBus.Emit 仍输出
    }

    [Fact]
    public async Task AnyVerifierReceivesSenderEmail_ConfirmsBatch()
    {
        // 3 个 verifier：FakeImapPoller 全局返回命中 → 任意一个 verifier 拉到都算命中
        var senderId = InsertSender("a@x.com");
        InsertVerifier("v1@y.com");
        InsertVerifier("v2@y.com");
        InsertVerifier("v3@y.com");

        _recipientRepo.InsertBatch(new[] { new Recipient { Email = "r1@x.com" } }, sourceFile: null);
        var all = _recipientRepo.GetByStatus("pending", 10);
        _recipientRepo.MarkUnverified(all.Select(r => r.Id), "batch-mv");

        var batch = new BatchTracker("batch-mv", DateTime.UtcNow.AddSeconds(-200), senderId,
            all.Select(r => r.Id).ToList());

        var imap = new FakeImapPoller(new[] { ("a@x.com", (uint)1, (DateTimeOffset?)DateTimeOffset.UtcNow) });
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 2 },
            imap, _logs.Add);

        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        Assert.Equal(BatchState.Verified, batch.State);
        // 不再触发扩选逻辑，且不会立即 Unconfirmed（IMAP 命中路径）；状态机已覆盖
    }

    [Fact]
    public async Task BatchExceedsLookbackMinutes_ForceUnconfirmed()
    {
        // 批次 SentAt 远超 LookbackMinutes=5 → 立即 Unconfirmed（兜底机制）
        var senderId = InsertSender("cool@x.com");
        InsertVerifier("v1@y.com");
        InsertVerifier("v2@y.com");

        _recipientRepo.InsertBatch(new[] { new Recipient { Email = "r1@x.com" } }, sourceFile: null);
        var all = _recipientRepo.GetByStatus("pending", 10);
        _recipientRepo.MarkUnverified(all.Select(r => r.Id), "batch-dbl");

        // SentAt 10 分钟前，远超 LookbackMinutes=5
        var batch = new BatchTracker("batch-dbl", DateTime.UtcNow.AddMinutes(-10), senderId,
            all.Select(r => r.Id).ToList());

        var imap = new FakeImapPoller(Array.Empty<(string, uint, DateTimeOffset?)>());
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig
            {
                RecheckIntervalSeconds = 180,
                MaxPollAttempts = 2,
                LookbackMinutes = 5,
                UnconfirmedCooldownMinutes = 60,
            }, imap, _logs.Add);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        Assert.Equal(BatchState.Unconfirmed, batch.State);
        Assert.Equal(1, batch.UnconfirmedAttempts);
        // 文件日志不再输出 [unconfirmed]（计划 Task 2 已精简）；UI 日志通过 LogBus.Emit 仍输出
    }

    [Fact]
    public async Task Hit_AnyVerifier_ConfirmsBatch()
    {
        // 2 个 verifier，fake poller 让 v2 返回命中，v1 返回空
        var senderId = InsertSender("multi@x.com");
        var v1Id = InsertVerifier("v1@y.com");
        var v2Id = InsertVerifier("v2@y.com");

        _recipientRepo.InsertBatch(new[] { new Recipient { Email = "r1@x.com" } }, sourceFile: null);
        var all = _recipientRepo.GetByStatus("pending", 10);
        _recipientRepo.MarkUnverified(all.Select(r => r.Id), "batch-hit-any");

        var batch = new BatchTracker("batch-hit-any", DateTime.UtcNow.AddSeconds(-200), senderId,
            all.Select(r => r.Id).ToList());

        // 用路由式 mock：按 verifier.Id 决定返回值
        var imap = new VerifierRoutedImap(new Dictionary<long, (string, uint, DateTimeOffset?)[]>
        {
            [v1Id] = Array.Empty<(string, uint, DateTimeOffset?)>(),
            [v2Id] = new[] { ("multi@x.com", (uint)1, (DateTimeOffset?)DateTimeOffset.UtcNow) },
        });
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 2 }, imap, _logs.Add);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        Assert.Equal(BatchState.Verified, batch.State);
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

    [Fact]
    public async Task ColdStart_RestoresUnconfirmedAttempts_FromDB()
    {
        // 模拟数据库里有 unverified 行，unconfirmed_count=2（已经 unconfirmed 过 2 次）
        // 重启后 ColdStartAsync 应从 max(unconfirmed_count) 恢复 UnconfirmedAttempts
        var senderId = InsertSender("a@x.com");
        InsertVerifier("v@y.com");

        _recipientRepo.InsertBatch(new[] { new Recipient { Email = "r1@x.com" } }, sourceFile: null);
        var id = _recipientRepo.GetAll()[0].Id;
        _recipientRepo.MarkUnverified(new[] { id }, "batch-cold");

        // 直接 UPDATE unconfirmed_count 模拟"已经 unconfirmed 两次"
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"UPDATE recipients SET unconfirmed_count=2 WHERE email='r1@x.com'";
            cmd.ExecuteNonQuery();
        }

        // 启动 worker（走 ColdStartAsync）
        var imap = new FakeImapPoller(Array.Empty<(string, uint, DateTimeOffset?)>());
        using var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool, new VerifierConfig(), imap, _logs.Add);
        await worker.StartAsync(CancellationToken.None);

        // StartAsync 走 Task.Run 异步跑 ColdStart，需轮询等待批次注册完成。
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (worker.ActiveBatches.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        var batch = worker.ActiveBatches.Single();
        Assert.Equal(2, batch.UnconfirmedAttempts);

        worker.Stop();
    }
}