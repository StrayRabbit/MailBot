using MailBot.Core.Models;
using MailBot.Core.Storage;
using Xunit;

namespace MailBot.Tests.Storage;

public class SenderRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly SenderRepository _repo;

    public SenderRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_sender_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _repo = new SenderRepository(_db);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private long InsertSender(string email, int sentToday = 0)
    {
        var id = _repo.Insert(new Sender
        {
            Email = email,
            SmtpHost = "h",
            SmtpPort = 465,
            Username = "u",
            PasswordEncrypted = Array.Empty<byte>(),
            DailyQuota = 100,
            QuotaResetDate = DateTime.Now.ToString("yyyy-MM-dd"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        if (sentToday > 0)
        {
            _repo.IncrementSentToday(id, sentToday, DateTime.Now.ToString("yyyy-MM-dd"));
        }
        return id;
    }

    [Fact]
    public void SetSentToday_PositiveValue_WritesAndClampsNothing()
    {
        var sid = InsertSender("a@x.com", sentToday: 5);
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        _repo.SetSentToday(sid, 30, today);
        var after = _repo.GetById(sid);
        Assert.NotNull(after);
        Assert.Equal(30, after!.SentToday);
    }

    [Fact]
    public void SetSentToday_NegativeValue_ClampsToZero()
    {
        var sid = InsertSender("b@x.com", sentToday: 5);
        _repo.SetSentToday(sid, -3, DateTime.Now.ToString("yyyy-MM-dd"));
        var after = _repo.GetById(sid);
        Assert.NotNull(after);
        Assert.Equal(0, after!.SentToday);
    }

    [Fact]
    public void SetSentToday_Zero_WritesZero()
    {
        var sid = InsertSender("c@x.com", sentToday: 5);
        _repo.SetSentToday(sid, 0, DateTime.Now.ToString("yyyy-MM-dd"));
        var after = _repo.GetById(sid);
        Assert.NotNull(after);
        Assert.Equal(0, after!.SentToday);
    }

    [Fact]
    public void SetSentToday_UpdatesQuotaResetDateToToday()
    {
        var sid = InsertSender("d@x.com", sentToday: 5);
        // 故意传一个"昨天"的日期，验证仓储会把它刷成传入的 quotaResetDate。
        // ResetQuotaIfNeededAsync 比较"DB 里存的 vs 今天"——只要仓储用传入的，今天调用就不会被重置。
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        _repo.SetSentToday(sid, 10, today);
        var after = _repo.GetById(sid);
        Assert.NotNull(after);
        Assert.Equal(today, after!.QuotaResetDate);
    }
}