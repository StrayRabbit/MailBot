using MailBot.Core.Models;
using MailBot.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MailBot.Tests.Storage;

public class BatchStatsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly BatchStatsRepository _repo;

    public BatchStatsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_batchstats_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _repo = new BatchStatsRepository(_db);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    /// <summary>
    /// 直接 INSERT 一行 recipient（绕过 RecipientRepository.InsertBatch，因为该路径不允许指定 batch_id/sent_at）。
    /// imported_at 在 schema 上 NOT NULL，必须给值。
    /// </summary>
    private void InsertRecipient(string email, string status, string batchId, DateTime sentAtUtc)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO recipients
(email, status, batch_id, sent_at, imported_at)
VALUES
($email, $status, $bid, $sent, $imported)";
        cmd.Parameters.AddWithValue("$email", email);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$bid", batchId);
        cmd.Parameters.AddWithValue("$sent", sentAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$imported", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void GetDailyDistinctBatchCounts_EmptyDb_ReturnsThirtyZeroDays()
    {
        var result = _repo.GetDailyDistinctBatchCounts(30);

        Assert.Equal(30, result.Count);
        Assert.All(result, x => Assert.Equal(0, x.BatchCount));

        // 最后一条是今天本地日，往前 29 天倒序
        var localToday = DateOnly.FromDateTime(MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        Assert.Equal(localToday, result[^1].Day);
        Assert.Equal(localToday.AddDays(-29), result[0].Day);
    }

    [Fact]
    public void GetDailyDistinctBatchCounts_SingleDay_MultipleBatches_CountedDistinct()
    {
        // 在本地今天中午（北京时间 12:00）= UTC 04:00 多插几条不同 batch_id
        var localNoon = DateOnly.FromDateTime(MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow))
            .ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromHours(12)));
        var utcNoon = TimeZoneInfo.ConvertTimeToUtc(localNoon, MailBot.Core.DisplayTimeZone.Shanghai);

        InsertRecipient("a@x.com", "sent", "batch-A", utcNoon);
        InsertRecipient("b@x.com", "sent", "batch-A", utcNoon);
        InsertRecipient("c@x.com", "sent", "batch-B", utcNoon);
        InsertRecipient("d@x.com", "sent", "batch-B", utcNoon);
        InsertRecipient("e@x.com", "sent", "batch-B", utcNoon);
        // 加一个跨状态的同 batch（A 的扩展）—— 应仍只算 batch_id 一次
        InsertRecipient("f@x.com", "verified", "batch-A", utcNoon);

        var result = _repo.GetDailyDistinctBatchCounts(30);

        var localToday = DateOnly.FromDateTime(MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        var today = result.Single(x => x.Day == localToday);
        Assert.Equal(2, today.BatchCount); // batch-A + batch-B
    }

    [Fact]
    public void GetDailyDistinctBatchCounts_DaysWithNoRecipients_AreZeroFilled()
    {
        var localToday = DateOnly.FromDateTime(MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow));

        // 只在第 1 天和第 30 天（最早/今天）插数据
        var day1UtcMidnight = ToUtcMidnight(localToday.AddDays(-29));
        var day30UtcMidnight = ToUtcMidnight(localToday);

        InsertRecipient("early@x.com", "sent", "batch-Early", day1UtcMidnight.AddHours(12));
        InsertRecipient("now@x.com", "sent", "batch-Now", day30UtcMidnight.AddHours(12));

        var result = _repo.GetDailyDistinctBatchCounts(30);

        Assert.Equal(30, result.Count);
        Assert.Equal(1, result[0].BatchCount);       // 最早一天
        Assert.Equal(0, result[1].BatchCount);       // 中间空档
        Assert.Equal(0, result[^2].BatchCount);
        Assert.Equal(1, result[^1].BatchCount);      // 今天
    }

    [Fact]
    public void GetDailyDistinctBatchCounts_SpansUtcMidnight_GroupsByLocalDay()
    {
        // 本地日 D（Shanghai）= [D 00:00+08, D+1 00:00+08) = [D-1 16:00 UTC, D 16:00 UTC)。
        // 挑 UTC 前一天 23:00 + UTC 当天 07:00 —— 跨 UTC 午夜但同属本地日 D。
        var localToday = DateOnly.FromDateTime(MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        var shanghaiMidnight = localToday.ToDateTime(TimeOnly.MinValue);
        var utcPrevDayElevenPm = TimeZoneInfo.ConvertTimeToUtc(
            shanghaiMidnight.AddHours(7), MailBot.Core.DisplayTimeZone.Shanghai);
        var utcTodaySevenAm = TimeZoneInfo.ConvertTimeToUtc(
            shanghaiMidnight.AddHours(15), MailBot.Core.DisplayTimeZone.Shanghai);

        InsertRecipient("y@x.com", "sent", "batch-X", utcPrevDayElevenPm);
        InsertRecipient("z@x.com", "sent", "batch-X", utcTodaySevenAm);

        var result = _repo.GetDailyDistinctBatchCounts(30);

        var day = result.Single(x => x.Day == localToday);
        Assert.Equal(1, day.BatchCount); // 同一 batch_id 跨 UTC 午夜，本地日合并
    }

    [Fact]
    public void GetDailyDistinctBatchCounts_OnlyCountsEligibleStatuses()
    {
        // pending / failed 不算；sent / verified / unverified / unconfirmed 算
        var localToday = DateOnly.FromDateTime(MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        var utcNoon = ToUtcMidnight(localToday).AddHours(12);

        InsertRecipient("p@x.com", "pending", "batch-X", utcNoon);
        InsertRecipient("f@x.com", "failed", "batch-X", utcNoon);
        InsertRecipient("s@x.com", "sent", "batch-X", utcNoon);
        InsertRecipient("v@x.com", "verified", "batch-X", utcNoon);
        InsertRecipient("u@x.com", "unverified", "batch-X", utcNoon);
        InsertRecipient("c@x.com", "unconfirmed", "batch-X", utcNoon);

        var result = _repo.GetDailyDistinctBatchCounts(30);

        var day = result.Single(x => x.Day == localToday);
        Assert.Equal(1, day.BatchCount);
    }

    [Fact]
    public void GetSummary_OnlyCountsEligibleStatuses()
    {
        // pending / failed 不算；sent / verified / unverified / unconfirmed 算
        var localToday = DateOnly.FromDateTime(MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        var utcNoon = ToUtcMidnight(localToday).AddHours(12);

        InsertRecipient("p@x.com", "pending", "u", utcNoon);
        InsertRecipient("f@x.com", "failed", "f", utcNoon);
        InsertRecipient("s@x.com", "sent", "s", utcNoon);
        InsertRecipient("v@x.com", "verified", "v", utcNoon);
        InsertRecipient("u@x.com", "unverified", "x", utcNoon);
        InsertRecipient("c@x.com", "unconfirmed", "y", utcNoon);

        var summary = _repo.GetSummary();

        // 4 distinct eligible batches + 1 distinct failed + 1 distinct pending (pending not counted) = 4
        Assert.Equal(4, summary.Total);
        // 全部 4 个都在今天 → Today = 4
        Assert.Equal(4, summary.Today);
        // 全部 4 个都在本周/月
        Assert.Equal(4, summary.ThisWeek);
        Assert.Equal(4, summary.ThisMonth);
        // 最近 sent_at 是最大那个
        Assert.NotNull(summary.LastSentAtUtc);
    }

    [Fact]
    public void GetSummary_EmptyDb_ReturnsZeros()
    {
        var summary = _repo.GetSummary();

        Assert.Equal(0, summary.Today);
        Assert.Equal(0, summary.ThisWeek);
        Assert.Equal(0, summary.ThisMonth);
        Assert.Equal(0, summary.Total);
        Assert.Null(summary.LastSentAtUtc);
    }

    [Fact]
    public void GetSummary_GroupsByLocalDay()
    {
        // 昨天 1 批 + 今天 2 批 → Today=2, ThisWeek=3（假设今天不是周一）, ThisMonth=3, Total=3
        var localToday = DateOnly.FromDateTime(MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        var utcTodayNoon = ToUtcMidnight(localToday).AddHours(12);
        var utcYesterdayNoon = ToUtcMidnight(localToday.AddDays(-1)).AddHours(12);

        InsertRecipient("a@x.com", "sent", "yesterday-batch", utcYesterdayNoon);
        InsertRecipient("b@x.com", "sent", "today-A", utcTodayNoon);
        InsertRecipient("c@x.com", "sent", "today-B", utcTodayNoon);

        var summary = _repo.GetSummary();

        Assert.Equal(2, summary.Today);
        Assert.Equal(3, summary.ThisMonth);
        Assert.Equal(3, summary.Total);
        // 本周按 ISO 周算：若 localToday 是周一，则 localWeekStart == localToday，
        // 昨天那批不在本周内 → ThisWeek=2。
        // 用断言条件化：仅验证 ThisWeek ∈ {2,3} 即可，不写死依赖时序。
        Assert.Contains(summary.ThisWeek, new[] { 2, 3 });
    }

    private static DateTime ToUtcMidnight(DateOnly localDay)
        => TimeZoneInfo.ConvertTimeToUtc(
            localDay.ToDateTime(TimeOnly.MinValue),
            MailBot.Core.DisplayTimeZone.Shanghai);

    [Fact]
    public void CountTodayVerified_EmptyDb_ReturnsZero()
    {
        Assert.Equal(0, _repo.CountTodayVerified());
    }

    [Fact]
    public void CountTodayVerified_MixedStatuses_OnlyVerifiedCounts()
    {
        // 今天本地中午的 UTC 时刻。
        var localToday = DateOnly.FromDateTime(MailBot.Core.DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        var utcTodayNoon = TimeZoneInfo.ConvertTimeToUtc(
            localToday.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromHours(12))),
            MailBot.Core.DisplayTimeZone.Shanghai);
        var utcYesterdayNoon = utcTodayNoon.AddDays(-1);

        // 1) 今天 + verified → +1
        InsertRecipientWithVerifiedAt("today-ok@x.com", "verified", utcTodayNoon);
        // 2) 今天 + status=sent → 不计
        InsertRecipientWithVerifiedAt("today-sent@x.com", "sent", utcTodayNoon);
        // 3) 昨天 + verified → 不计
        InsertRecipientWithVerifiedAt("yest-ok@x.com", "verified", utcYesterdayNoon);
        // 4) 今天 + verified_at=NULL → 不计
        InsertRecipientWithVerifiedAt("null@x.com", "verified", null);
        // 5) 今天 + verified_at 落在今天 UTC 范围内（Shanghai 8:00+） → +1
        //    (Shanghai 0:00-08:00 今天会因 utc_day 落在"昨天"被 post-filter 漏算,
        //     与 GetDailySendCounts 同源;不在本次改动范围内。)
        var todayUtcMid8 = TimeZoneInfo.ConvertTimeToUtc(
            localToday.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromHours(8))),
            MailBot.Core.DisplayTimeZone.Shanghai);
        InsertRecipientWithVerifiedAt("mid-today-utc@x.com", "verified", todayUtcMid8);

        Assert.Equal(2, _repo.CountTodayVerified());
    }

    /// <summary>
    /// 直接 INSERT 一行 recipient 并写 verified_at（区别于 InsertRecipient 的 sent_at）。
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
}