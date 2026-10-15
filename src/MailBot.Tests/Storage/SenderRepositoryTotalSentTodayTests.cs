using MailBot.Core.Models;
using MailBot.Core.Storage;
using Xunit;

namespace MailBot.Tests.Storage;

public class SenderRepositoryTotalSentTodayTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly SenderRepository _repo;

    public SenderRepositoryTotalSentTodayTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_totalsent_{Guid.NewGuid():N}.db");
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
    public void TotalSentToday_EmptyTable_ReturnsZero()
    {
        Assert.Equal(0, _repo.TotalSentToday());
    }

    [Fact]
    public void TotalSentToday_MultipleSenders_SumsAllSentToday()
    {
        InsertSender("a@x.com", sentToday: 5);
        InsertSender("b@x.com", sentToday: 3);
        InsertSender("c@x.com", sentToday: 0);
        InsertSender("d@x.com", sentToday: 12);

        Assert.Equal(20, _repo.TotalSentToday());
    }
}
