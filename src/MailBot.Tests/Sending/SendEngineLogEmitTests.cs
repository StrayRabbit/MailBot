using MailBot.App.Logging;
using MailBot.Core.Logging;
using MailBot.Core.Mail;
using MailBot.Core.Models;
using MailBot.Core.Sending;
using MailBot.Core.Senders;
using MailBot.Core.Storage;
using MailBot.Core.Verification;
using MimeKit;
using Xunit;

namespace MailBot.Tests.Sending;

// 共享 LogBus 单例：串行运行避免并发测试相互污染。
[Collection("LogBus")]
public class SendEngineLogEmitTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly SenderRepository _senderRepo;
    private readonly RecipientRepository _recipientRepo;
    private readonly VerifierRepository _verifierRepo;
    private readonly SenderPool _senderPool;
    private readonly List<LogEntry> _emits = new();

    public SendEngineLogEmitTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_sendlogemit_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _senderRepo = new SenderRepository(_db);
        _recipientRepo = new RecipientRepository(_db);
        _verifierRepo = new VerifierRepository(_db);
        _senderPool = new SenderPool(_senderRepo);

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
            DailyQuota = 100,
            QuotaResetDate = DateTime.Now.ToString("yyyy-MM-dd"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    private void InsertRecipients(int n)
    {
        var list = new List<Recipient>(n);
        for (int i = 0; i < n; i++)
        {
            list.Add(new Recipient { Email = $"r{i}@x.com", ImportedAt = DateTime.UtcNow });
        }
        _recipientRepo.InsertBatch(list, sourceFile: null);
    }

    private void InsertVerifier(string email)
    {
        _verifierRepo.Insert(new VerifierAccount
        {
            Email = email, ImapHost = "i", ImapPort = 993, Username = email,
            PasswordEncrypted = Array.Empty<byte>(), Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>成功的 dispatcher（用于断言 Success 级别 Emit）。</summary>
    private sealed class AlwaysOkDispatcher : SmtpDispatcher
    {
        public override Task<SmtpSendResult> SendAsync(Sender s, MimeMessage m, CancellationToken ct)
            => Task.FromResult(new SmtpSendResult { Category = SmtpErrorCategory.Success, DurationMs = 10 });
    }

    [Fact]
    public async Task SendSuccess_Emits_Success_With_ShortCode()
    {
        var sid = InsertSender("sender@example.com");
        InsertVerifier("v@y.com");
        InsertRecipients(3);

        var imapPoller = new MockImap();
        var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig(), imapPoller, _ => { }, uiLogger: LogBus.Instance);
        using (worker)
        {
            await worker.StartAsync(CancellationToken.None);
            worker.Stop();
        }

        var template = new MailTemplate { Subject = "测试", HtmlBody = "<p>x</p>" };
        var cfg = new AppConfig
        {
            WindowStart = "00:00", WindowEnd = "23:59",
            SendIntervalSeconds = 0, BatchSize = 3,
            MaxRetries = 1, CoolingFailures = 3, CoolingMinutes = 10,
        };

        var dispatcher = new AlwaysOkDispatcher();
        var executor = new BatchExecutor(dispatcher, _recipientRepo, _senderRepo, _verifierRepo, new VerifierConfig(), uiLogger: LogBus.Instance);
        var engine = new SendEngine(
            _db, _senderRepo, _recipientRepo, _senderPool, dispatcher,
            _verifierRepo, worker, new VerifierConfig(), executor, new SenderFailureClassifier(),
            uiLogger: LogBus.Instance);

        using var cts = new CancellationTokenSource();
        // 进入循环后立即取消 → Task.Delay(30s) 立即抛 TaskCanceledException，
        // 但 _uiLogger.Emit(Success) 在 5a 段已发出，所以 cancel 之前已落入 _emits。
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await engine.RunAsync(template, cfg, cts.Token);
        }
        catch (OperationCanceledException) { /* 预期 */ }

        // 至少有一个 Success 条目
        var success = _emits.FirstOrDefault(e => e.Level == LogLevel.Success && e.Message.Contains("发送"));
        Assert.NotNull(success);
        Assert.Matches(@"…[\da-f]{4}", success!.Message);
    }

    private sealed class MockImap : IImapPoller
    {
        public Task<IReadOnlyList<(string FromEmail, uint Uid, DateTimeOffset? InternalDate)>>
            PollSinceAsync(VerifierAccount v, DateTimeOffset r, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(string, uint, DateTimeOffset?)>>(Array.Empty<(string, uint, DateTimeOffset?)>());
        public Task<(bool, string?)> TestConnectionAsync(VerifierAccount v, CancellationToken ct)
            => Task.FromResult((true, (string?)null));
        public Task<uint?> GetInboxMaxUidAsync(VerifierAccount v, CancellationToken ct)
            => Task.FromResult<uint?>(null);
    }
}