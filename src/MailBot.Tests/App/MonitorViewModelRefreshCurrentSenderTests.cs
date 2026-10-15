using MailBot.App.ViewModels;
using MailBot.Core.Models;
using MailBot.Core.Storage;
using Xunit;

namespace MailBot.Tests.App;

/// <summary>
/// 覆盖「切到监控页时刷新当前展示账号」：用户改了某个账号的 SentToday / 配额后，
/// 切到监控页应立即更新 Monitor.CurrentSenderSent / CurrentSenderQuota，
/// 否则顶部与监控页"账号配额"行的数字会停留在旧值，看起来"今日已发没刷新"。
/// </summary>
public class MonitorViewModelRefreshCurrentSenderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;

    public MonitorViewModelRefreshCurrentSenderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_monrefresh_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private long InsertSender(SenderRepository repo, int dailyQuota = 50, int sentToday = 0)
    {
        var id = repo.Insert(new Sender
        {
            Email = $"u{Guid.NewGuid():N}@x.com",
            SmtpHost = "h",
            SmtpPort = 465,
            Username = "u",
            PasswordEncrypted = Array.Empty<byte>(),
            DailyQuota = dailyQuota,
            QuotaResetDate = DateTime.Now.ToString("yyyy-MM-dd"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        if (sentToday > 0)
        {
            repo.IncrementSentToday(id, sentToday, DateTime.Now.ToString("yyyy-MM-dd"));
        }
        return id;
    }

    [Fact]
    public void RefreshCurrentSenderInfo_PicksUpNewSentToday()
    {
        var senderRepo = new SenderRepository(_db);
        var id = InsertSender(senderRepo, dailyQuota: 50, sentToday: 5);
        var email = senderRepo.GetAll().Single().Email;

        var vm = new MonitorViewModel(new RecipientRepository(_db), senderRepo, new BatchStatsRepository(_db));
        // 模拟"批次完成过"的初始状态：监控页记住了上次发件账号
        vm.RefreshSenderInfo(email);
        Assert.Equal(5, vm.CurrentSenderSent);
        Assert.Equal(50, vm.CurrentSenderQuota);

        // 用户在账号页改了 SentToday
        senderRepo.SetSentToday(id, 30, DateTime.Now.ToString("yyyy-MM-dd"));

        // 切到监控页触发的刷新路径
        vm.RefreshCurrentSenderInfo();

        Assert.Equal(30, vm.CurrentSenderSent);
        Assert.Equal(50, vm.CurrentSenderQuota);
    }

    [Fact]
    public void RefreshCurrentSenderInfo_PicksUpNewDailyQuota()
    {
        var senderRepo = new SenderRepository(_db);
        var id = InsertSender(senderRepo, dailyQuota: 50, sentToday: 5);
        var email = senderRepo.GetAll().Single().Email;

        var vm = new MonitorViewModel(new RecipientRepository(_db), senderRepo, new BatchStatsRepository(_db));
        vm.RefreshSenderInfo(email);
        Assert.Equal(50, vm.CurrentSenderQuota);

        // 用户在编辑账号对话框改了 DailyQuota → 走 _repo.Update(s)
        var sender = senderRepo.GetAll().Single();
        sender.DailyQuota = 200;
        senderRepo.Update(sender);

        vm.RefreshCurrentSenderInfo();

        Assert.Equal(200, vm.CurrentSenderQuota);
    }

    [Fact]
    public void RefreshCurrentSenderInfo_NoOp_WhenNoSenderShown()
    {
        var senderRepo = new SenderRepository(_db);
        var vm = new MonitorViewModel(new RecipientRepository(_db), senderRepo, new BatchStatsRepository(_db));

        // 未运行过批次，CurrentSenderEmail 为空 —— 不应抛异常
        vm.RefreshCurrentSenderInfo();

        Assert.Equal(string.Empty, vm.CurrentSenderEmail);
    }
}