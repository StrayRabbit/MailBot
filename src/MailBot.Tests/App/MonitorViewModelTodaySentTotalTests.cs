using MailBot.App.ViewModels;
using MailBot.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MailBot.Tests.App;

/// <summary>
/// 监控页"今日已发"卡片的口径 = 统计页"今日"柱（recipients.status='verified' AND
/// verified_at 在今天本地日）。IMAP 命中时 BatchVerified 触发 RefreshStats，
/// 新 verified_at 行才被计入；BatchCompleted 这一刻 verified_at 还未写，数字不变。
/// </summary>
public class MonitorViewModelTodaySentTotalTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;

    public MonitorViewModelTodaySentTotalTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_monitortotal_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    /// <summary>
    /// 直接 INSERT 一行 recipient，绕过 RecipientRepository.InsertBatch（不允许指定 verified_at）。
    /// imported_at 在 schema 上 NOT NULL，必须给值。
    /// </summary>
    private void InsertRecipientWithVerifiedAt(string email, string status, DateTime? verifiedAtUtc)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO recipients
(email, status, verified_at, imported_at)
VALUES
($email, $status, $verified, $imported)";
        cmd.Parameters.AddWithValue("$email", email);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$verified",
            verifiedAtUtc is null ? DBNull.Value : verifiedAtUtc.Value.ToString("o"));
        cmd.Parameters.AddWithValue("$imported", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Constructor_TodaySentTotal_CountsOnlyVerifiedInTodayLocalDay()
    {
        // 本地今天中午（北京时间 12:00）= UTC 04:00。
        var localToday = MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow).Date;
        var utcTodayNoon = TimeZoneInfo.ConvertTimeToUtc(
            localToday.AddHours(12), MailBot.Core.DisplayTimeZone.Shanghai);
        var utcYesterdayNoon = utcTodayNoon.AddDays(-1);

        // 今天 + verified → +1
        InsertRecipientWithVerifiedAt("today-ok@x.com", "verified", utcTodayNoon);
        // 今天 + status=sent → 不计（status 不对）
        InsertRecipientWithVerifiedAt("today-sent@x.com", "sent", utcTodayNoon);
        // 昨天 + verified → 不计（日期不对）
        InsertRecipientWithVerifiedAt("yest-ok@x.com", "verified", utcYesterdayNoon);
        // 今天 + verified_at=NULL → 不计
        InsertRecipientWithVerifiedAt("null-ok@x.com", "verified", null);

        var vm = new MonitorViewModel(
            new RecipientRepository(_db),
            new SenderRepository(_db),
            new BatchStatsRepository(_db));

        Assert.Equal(1, vm.TodaySentTotal);
    }

    [Fact]
    public void Constructor_TodaySentTotal_CrossUtcBoundary_CountsByLocalDay()
    {
        // 跨 UTC 日界的 today 事件：Shanghai 16:00 今天 = UTC 08:00 今天。
        // SQL 截 utc_day = "今天"，C# 转本地日 = 今天，计入。
        // （注:Shanghai 0:00-08:00 今天会因 utc_day 落在"昨天"被 post-filter 漏算，
        //   这是 GetDailySendCounts 同源问题,不在本次改动范围内——保持现有行为一致。）
        var localToday = MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow).Date;
        var utcShanghai1600Today = TimeZoneInfo.ConvertTimeToUtc(
            localToday.AddHours(16),
            MailBot.Core.DisplayTimeZone.Shanghai);
        var utcShanghai2000Today = TimeZoneInfo.ConvertTimeToUtc(
            localToday.AddHours(20),
            MailBot.Core.DisplayTimeZone.Shanghai);

        InsertRecipientWithVerifiedAt("mid-today-1@x.com", "verified", utcShanghai1600Today);
        InsertRecipientWithVerifiedAt("mid-today-2@x.com", "verified", utcShanghai2000Today);

        var vm = new MonitorViewModel(
            new RecipientRepository(_db),
            new SenderRepository(_db),
            new BatchStatsRepository(_db));

        Assert.Equal(2, vm.TodaySentTotal);
    }

    [Fact]
    public void RefreshStats_TodaySentTotal_PicksUpNewVerified()
    {
        var localToday = MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow).Date;
        var utcTodayNoon = TimeZoneInfo.ConvertTimeToUtc(
            localToday.AddHours(12), MailBot.Core.DisplayTimeZone.Shanghai);

        var vm = new MonitorViewModel(
            new RecipientRepository(_db),
            new SenderRepository(_db),
            new BatchStatsRepository(_db));
        Assert.Equal(0, vm.TodaySentTotal);

        InsertRecipientWithVerifiedAt("a@x.com", "verified", utcTodayNoon);
        InsertRecipientWithVerifiedAt("b@x.com", "verified", utcTodayNoon);
        vm.RefreshStats();
        Assert.Equal(2, vm.TodaySentTotal);

        InsertRecipientWithVerifiedAt("c@x.com", "verified", utcTodayNoon);
        vm.RefreshStats();
        Assert.Equal(3, vm.TodaySentTotal);
    }

    [Fact]
    public void RefreshStats_TodaySentTotal_BatchCompletedDoesNotBumpCount()
    {
        // BCC 有验证模式下 BatchCompleted 这一刻 verified_at 还没写，
        // 只到 unverified；数字不变是预期。
        var localToday = MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow).Date;
        var utcTodayNoon = TimeZoneInfo.ConvertTimeToUtc(
            localToday.AddHours(12), MailBot.Core.DisplayTimeZone.Shanghai);

        InsertRecipientWithVerifiedAt("ok@x.com", "verified", utcTodayNoon);

        var vm = new MonitorViewModel(
            new RecipientRepository(_db),
            new SenderRepository(_db),
            new BatchStatsRepository(_db));
        Assert.Equal(1, vm.TodaySentTotal);

        // 模拟 BatchCompleted 落地：写一行 unverified，没有 verified_at。
        InsertRecipientWithVerifiedAt("pending@x.com", "unverified", null);
        vm.RefreshStats();
        Assert.Equal(1, vm.TodaySentTotal);
    }

    [Fact]
    public void RefreshStats_TodaySentTotal_UnconfirmedDropsFromCount()
    {
        // 一行 verified 改 unconfirmed 后 status 不再是 'verified'，数字应下降。
        var localToday = MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow).Date;
        var utcTodayNoon = TimeZoneInfo.ConvertTimeToUtc(
            localToday.AddHours(12), MailBot.Core.DisplayTimeZone.Shanghai);

        InsertRecipientWithVerifiedAt("a@x.com", "verified", utcTodayNoon);

        var vm = new MonitorViewModel(
            new RecipientRepository(_db),
            new SenderRepository(_db),
            new BatchStatsRepository(_db));
        Assert.Equal(1, vm.TodaySentTotal);

        UpdateStatus("a@x.com", "unconfirmed");
        vm.RefreshStats();
        Assert.Equal(0, vm.TodaySentTotal);
    }

    private void UpdateStatus(string email, string newStatus)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE recipients SET status = $s WHERE email = $e";
        cmd.Parameters.AddWithValue("$s", newStatus);
        cmd.Parameters.AddWithValue("$e", email);
        cmd.ExecuteNonQuery();
    }
}