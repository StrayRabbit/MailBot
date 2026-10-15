using MailBot.Core.Models;
using MailBot.Core.Senders;
using MailBot.Core.Storage;
using Xunit;

namespace MailBot.Tests.Senders;

public class SendersViewModelTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly SenderRepository _repo;

    public SendersViewModelTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_test_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _repo = new SenderRepository(_db);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private long InsertBanned(string email)
    {
        return _repo.Insert(new Sender
        {
            Email = email,
            SmtpHost = "smtp.x.com",
            SmtpPort = 465,
            UseSsl = true,
            Username = email,
            PasswordEncrypted = Array.Empty<byte>(),
            DailyQuota = 50,
            QuotaResetDate = "2026-09-26",
            ConsecutiveFailures = 5,
            Status = SenderStatus.Banned,
            CooldownUntil = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    [Fact]
    public void Unban_BannedSender_ResetsToNormalInDbAndInList()
    {
        var id = InsertBanned("banned@x.com");

        var vm = new MailBot.App.ViewModels.SendersViewModel(_repo);
        var banned = vm.Senders.First(s => s.Id == id);
        Assert.Equal(SenderStatus.Banned, banned.Status);

        // 触发解封
        vm.Unban(banned);

        // 仓储里已变 normal
        var after = _repo.GetById(id);
        Assert.Equal(SenderStatus.Normal, after!.Status);
        Assert.Equal(0, after.ConsecutiveFailures);
        Assert.Null(after.CooldownUntil);

        // VM 列表里也已刷新
        var refreshed = vm.Senders.First(s => s.Id == id);
        Assert.Equal(SenderStatus.Normal, refreshed.Status);
        Assert.Equal(0, refreshed.ConsecutiveFailures);
        Assert.Null(refreshed.CooldownUntil);
    }
}