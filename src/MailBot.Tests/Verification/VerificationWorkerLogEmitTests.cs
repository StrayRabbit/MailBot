using MailBot.App.Logging;
using MailBot.Core.Logging;
using MailBot.Core.Models;
using MailBot.Core.Senders;
using MailBot.Core.Sending;
using MailBot.Core.Storage;
using MailBot.Core.Verification;
using Xunit;

namespace MailBot.Tests.Verification;

// 共享 LogBus 单例：串行运行避免并发测试相互污染。
[Collection("LogBus")]
public class VerificationWorkerLogEmitTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly VerifierRepository _verifierRepo;
    private readonly RecipientRepository _recipientRepo;
    private readonly SenderRepository _senderRepo;
    private readonly SenderPool _senderPool;
    private readonly List<LogEntry> _emits = new();

    public VerificationWorkerLogEmitTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_logemit_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _verifierRepo = new VerifierRepository(_db);
        _recipientRepo = new RecipientRepository(_db);
        _senderRepo = new SenderRepository(_db);
        _senderPool = new SenderPool(_senderRepo);

        // 订阅 LogBus 捕获 Emit（清空避免其他测试残留）
        LogBus.Instance.ResetForTest();
        LogBus.Instance.LogEmitted += (_, e) => _emits.Add(e);
    }

    public void Dispose()
    {
        LogBus.Instance.ResetForTest();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private long InsertSender(string email)
    {
        return _senderRepo.Insert(new Sender
        {
            Email = email, SmtpHost = "h", SmtpPort = 465, Username = "u",
            PasswordEncrypted = Array.Empty<byte>(),
            DailyQuota = 50,
            QuotaResetDate = "2026-09-26",
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

    private sealed class FakeImap : IImapPoller
    {
        private readonly IReadOnlyList<(string, uint, DateTimeOffset?)> _items;
        public FakeImap(IEnumerable<(string, uint, DateTimeOffset?)> items) => _items = items.ToList();
        public Task<IReadOnlyList<(string FromEmail, uint Uid, DateTimeOffset? InternalDate)>>
            PollSinceAsync(VerifierAccount v, DateTimeOffset r, CancellationToken ct)
            => Task.FromResult(_items);
        public Task<(bool, string?)> TestConnectionAsync(VerifierAccount v, CancellationToken ct)
            => Task.FromResult((true, (string?)null));
        public Task<uint?> GetInboxMaxUidAsync(VerifierAccount v, CancellationToken ct)
            => Task.FromResult<uint?>(null);
    }

    private Task InvokePollAsync(VerificationWorker worker)
    {
        var m = typeof(VerificationWorker).GetMethod("PollOnceAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (Task)m!.Invoke(worker, new object?[] { CancellationToken.None, false })!;
    }

    [Fact]
    public async Task Verified_Emits_Success_With_ShortCode()
    {
        var sid = InsertSender("a@x.com");
        InsertVerifier("v@y.com");
        // SentAt 200s 前 → 已过 RecheckIntervalSeconds
        var batch = new BatchTracker("2026-09-26-001-7bff", DateTime.UtcNow.AddSeconds(-200), sid, new long[] { 1 });
        var imap = new FakeImap(new[] { ("a@x.com", (uint)10, (DateTimeOffset?)DateTimeOffset.UtcNow) });
        using var worker = new VerificationWorker(
            _verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig { RecheckIntervalSeconds = 180 }, imap, _ => { },
            uiLogger: LogBus.Instance);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        var verified = _emits.SingleOrDefault(e => e.Level == LogLevel.Success);
        Assert.NotNull(verified);
        Assert.Contains("已验证", verified!.Message);
        Assert.Contains("…7bff", verified.Message);
    }

    [Fact]
    public async Task Unconfirmed_Emits_Warning_With_Account()
    {
        var sid = InsertSender("sender@example.com");
        InsertVerifier("v@y.com");
        var batch = new BatchTracker("2026-09-26-002-8f21", DateTime.UtcNow.AddSeconds(-200), sid, new long[] { 1 });
        // 合并发送后 batch 固定 1 个 verifier → MaxPollAttempts=1 即触发 Unconfirmed
        var imap = new FakeImap(Array.Empty<(string, uint, DateTimeOffset?)>()); // 永远不命中
        using var worker = new VerificationWorker(
            _verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig { RecheckIntervalSeconds = 180, MaxPollAttempts = 1 }, imap, _ => { },
            uiLogger: LogBus.Instance);
        worker.RegisterBatch(batch);

        await InvokePollAsync(worker);

        var warn = _emits.SingleOrDefault(e => e.Level == LogLevel.Warning);
        Assert.NotNull(warn);
        Assert.Contains("验证超时", warn!.Message);
        Assert.Equal("sender@example.com", warn.Account);
        Assert.Contains("…8f21", warn.Message);
    }
}