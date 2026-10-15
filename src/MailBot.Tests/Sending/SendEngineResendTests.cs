using MailBot.Core.Mail;
using MailBot.Core.Models;
using MailBot.Core.Sending;
using MailBot.Core.Senders;
using MailBot.Core.Storage;
using MailBot.Core.Verification;
using MimeKit;
using Xunit;

namespace MailBot.Tests.Sending;

/// <summary>
/// 测试 SendEngine.ResendBatchAsync（IResendHandler 实现）的各分支：
///   - 不扣配额、不退配额
///   - 不触发 BatchCompleted 事件
///   - 缺依赖时优雅失败
///   - SMTP 失败返回 false、tracker 不动
/// </summary>
public class SendEngineResendTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly SenderRepository _senderRepo;
    private readonly RecipientRepository _recipientRepo;
    private readonly VerifierRepository _verifierRepo;
    private readonly SenderPool _senderPool;
    private readonly List<string> _logs = new();

    public SendEngineResendTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_resend_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _senderRepo = new SenderRepository(_db);
        _recipientRepo = new RecipientRepository(_db);
        _verifierRepo = new VerifierRepository(_db);
        _senderPool = new SenderPool(_senderRepo);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private long InsertSender(string email, int dailyQuota = 100)
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

    private (long[] recipientIds, int[] insertedIds) InsertRecipients(int n)
    {
        var list = new List<Recipient>(n);
        for (int i = 0; i < n; i++)
        {
            list.Add(new Recipient { Email = $"r{i}@x.com", ImportedAt = DateTime.UtcNow });
        }
        var inserted = _recipientRepo.InsertBatch(list, sourceFile: null);
        // 拿到刚插入的 id（按 email 反查）：取所有 pending、按 email 排序
        var pending = _recipientRepo.GetByStatus("pending", limit: n);
        var ids = pending.Select(r => r.Id).OrderBy(s => s).Take(inserted).ToArray();
        return (ids, ids.Select(i => (int)i).ToArray());
    }

    /// <summary>Scripted dispatcher：每次 SendAsync 返回队列头结果。</summary>
    private sealed class ScriptedDispatcher : SmtpDispatcher
    {
        private readonly Queue<SmtpSendResult> _results;
        public ScriptedDispatcher(IEnumerable<SmtpSendResult> results)
            => _results = new Queue<SmtpSendResult>(results);
        public override Task<SmtpSendResult> SendAsync(Sender s, MimeMessage m, CancellationToken ct)
        {
            if (_results.Count == 0)
            {
                return Task.FromResult(new SmtpSendResult { Category = SmtpErrorCategory.Success, DurationMs = 1 });
            }
            return Task.FromResult(_results.Dequeue());
        }
    }

    /// <summary>SendEngine 装配：返回 engine，模板已设置。</summary>
    private SendEngine BuildEngine(SmtpDispatcher dispatcher)
    {
        var imap = new EmptyImapPoller();
        var worker = new VerificationWorker(_verifierRepo, _recipientRepo, _senderRepo, _senderPool,
            new VerifierConfig(), imap, _logs.Add);
        var executor = new BatchExecutor(dispatcher, _recipientRepo, _senderRepo, _verifierRepo, new VerifierConfig());
        var engine = new SendEngine(
            _db, _senderRepo, _recipientRepo, _senderPool, dispatcher,
            _verifierRepo, worker, new VerifierConfig(), executor, new SenderFailureClassifier(),
            uiLogger: null);
        return engine;
    }

    /// <summary>设置 _currentTemplate（私有字段）——通过反射；测试用。</summary>
    private static void SetCurrentTemplate(SendEngine engine, MailTemplate template)
    {
        var f = typeof(SendEngine).GetField("_currentTemplate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        f!.SetValue(engine, template);
    }

    private static BatchTracker BuildTracker(string batchId, long senderId, IReadOnlyList<long> recipientIds,
        long initialVerifierId, int charged = 0)
    {
        var t = new BatchTracker(batchId, DateTime.UtcNow, senderId, recipientIds,
            initialVerifierId: initialVerifierId);
        if (charged > 0) t.RecordChargedQuota(charged);
        return t;
    }

    [Fact]
    public async Task ResendBatchAsync_UpdatesBatchTracker_NotChargedQuota_NotSentToday()
    {
        var sid = InsertSender("sender@example.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");
        var (rIds, _) = InsertRecipients(3);

        var dispatcher = new ScriptedDispatcher(new[]
        {
            new SmtpSendResult { Category = SmtpErrorCategory.Success, DurationMs = 7 },
        });
        var engine = BuildEngine(dispatcher);
        SetCurrentTemplate(engine, new MailTemplate { Subject = "t", HtmlBody = "<p>x</p>" });

        var tracker = BuildTracker("batch-001", sid, rIds, v1, charged: 3);
        var sentBefore = _senderRepo.GetById(sid)!.SentToday;

        var ok = await engine.ResendBatchAsync(tracker, v2, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(2, tracker.AttemptedVerifierIds.Count);
        Assert.Contains(v1, tracker.AttemptedVerifierIds);
        Assert.Contains(v2, tracker.AttemptedVerifierIds);
        Assert.Equal(1, tracker.VerifierRetryCount);
        Assert.NotNull(tracker.ResentAt);
        // ChargedQuotaCount 保持原值
        Assert.Equal(3, tracker.ChargedQuotaCount);
        // sent_today 不变
        var sentAfter = _senderRepo.GetById(sid)!.SentToday;
        Assert.Equal(sentBefore, sentAfter);
    }

    [Fact]
    public async Task ResendBatchAsync_NullTemplate_FailsGracefully()
    {
        var sid = InsertSender("sender@example.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");
        var (rIds, _) = InsertRecipients(2);

        var dispatcher = new ScriptedDispatcher(new[] { new SmtpSendResult { Category = SmtpErrorCategory.Success, DurationMs = 1 } });
        var engine = BuildEngine(dispatcher);
        // _currentTemplate 保持 null

        var tracker = BuildTracker("batch-002", sid, rIds, v1);
        var ok = await engine.ResendBatchAsync(tracker, v2, CancellationToken.None);

        Assert.False(ok);
        Assert.Single(tracker.AttemptedVerifierIds);  // 没追加
        Assert.Equal(0, tracker.VerifierRetryCount);
    }

    [Fact]
    public async Task ResendBatchAsync_SmtpFails_ReturnsFalse_TrackerUntouched()
    {
        var sid = InsertSender("sender@example.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");
        var (rIds, _) = InsertRecipients(2);

        var dispatcher = new ScriptedDispatcher(new[]
        {
            new SmtpSendResult { Category = SmtpErrorCategory.TemporaryFailure, ErrorMessage = "smtp down", DurationMs = 1 },
        });
        var engine = BuildEngine(dispatcher);
        SetCurrentTemplate(engine, new MailTemplate { Subject = "t", HtmlBody = "<p>x</p>" });

        var tracker = BuildTracker("batch-003", sid, rIds, v1, charged: 2);
        var ok = await engine.ResendBatchAsync(tracker, v2, CancellationToken.None);

        Assert.False(ok);
        Assert.Single(tracker.AttemptedVerifierIds);  // 没追加
        Assert.Equal(0, tracker.VerifierRetryCount);
        Assert.Null(tracker.ResentAt);
    }

    // ResendBatchAsync_WritesPendingVerify_SendLogs 测试已随 send_logs 表移除——
// 重发分支现在只更新 tracker 状态（AttemptedVerifierIds / VerifierRetryCount / ResentAt），
// 不再写审计行。测试集合已在 SendEngineResendTests 中删除原条目。

    [Fact]
    public async Task ResendBatchAsync_DoesNotRaise_BatchCompletedEvent()
    {
        var sid = InsertSender("sender@example.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");
        var (rIds, _) = InsertRecipients(2);

        var dispatcher = new ScriptedDispatcher(new[]
        {
            new SmtpSendResult { Category = SmtpErrorCategory.Success, DurationMs = 1 },
        });
        var engine = BuildEngine(dispatcher);
        SetCurrentTemplate(engine, new MailTemplate { Subject = "t", HtmlBody = "<p>x</p>" });

        int raisedCount = 0;
        engine.BatchCompleted += (_, _) => Interlocked.Increment(ref raisedCount);

        var tracker = BuildTracker("batch-005", sid, rIds, v1);
        await engine.ResendBatchAsync(tracker, v2, CancellationToken.None);

        Assert.Equal(0, raisedCount);  // 重发路径不触发 BatchCompleted
    }

    [Fact]
    public async Task ResendBatchAsync_MissingVerifier_FailsGracefully()
    {
        var sid = InsertSender("sender@example.com");
        var v1 = InsertVerifier("v1@y.com");
        var (rIds, _) = InsertRecipients(2);

        var dispatcher = new ScriptedDispatcher(new[] { new SmtpSendResult { Category = SmtpErrorCategory.Success, DurationMs = 1 } });
        var engine = BuildEngine(dispatcher);
        SetCurrentTemplate(engine, new MailTemplate { Subject = "t", HtmlBody = "<p>x</p>" });

        var tracker = BuildTracker("batch-006", sid, rIds, v1);
        var ok = await engine.ResendBatchAsync(tracker, newVerifierId: 999_999L, CancellationToken.None);

        Assert.False(ok);
        Assert.Single(tracker.AttemptedVerifierIds);
    }

    [Fact]
    public async Task ResendBatchAsync_MissingSender_FailsGracefully()
    {
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");
        var (rIds, _) = InsertRecipients(2);

        var dispatcher = new ScriptedDispatcher(new[] { new SmtpSendResult { Category = SmtpErrorCategory.Success, DurationMs = 1 } });
        var engine = BuildEngine(dispatcher);
        SetCurrentTemplate(engine, new MailTemplate { Subject = "t", HtmlBody = "<p>x</p>" });

        var tracker = BuildTracker("batch-007", senderId: 999_999L, rIds, v1);
        var ok = await engine.ResendBatchAsync(tracker, v2, CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task ResendBatchAsync_RecipientsEmpty_FailsGracefully()
    {
        var sid = InsertSender("sender@example.com");
        var v1 = InsertVerifier("v1@y.com");
        var v2 = InsertVerifier("v2@y.com");

        var dispatcher = new ScriptedDispatcher(new[] { new SmtpSendResult { Category = SmtpErrorCategory.Success, DurationMs = 1 } });
        var engine = BuildEngine(dispatcher);
        SetCurrentTemplate(engine, new MailTemplate { Subject = "t", HtmlBody = "<p>x</p>" });

        // 用空 ids 列表
        var tracker = BuildTracker("batch-008", sid, Array.Empty<long>(), v1);
        var ok = await engine.ResendBatchAsync(tracker, v2, CancellationToken.None);

        Assert.False(ok);
    }

    private sealed class EmptyImapPoller : IImapPoller
    {
        public Task<IReadOnlyList<(string FromEmail, uint Uid, DateTimeOffset? InternalDate)>>
            PollSinceAsync(VerifierAccount v, DateTimeOffset r, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<(string, uint, DateTimeOffset?)>>(Array.Empty<(string, uint, DateTimeOffset?)>());
        public Task<(bool ok, string? error)> TestConnectionAsync(VerifierAccount v, CancellationToken ct)
            => Task.FromResult((true, (string?)null));
        public Task<uint?> GetInboxMaxUidAsync(VerifierAccount v, CancellationToken ct)
            => Task.FromResult<uint?>(null);
    }
}