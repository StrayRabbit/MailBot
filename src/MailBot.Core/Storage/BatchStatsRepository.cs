using MailBot.Core.Models;
using Microsoft.Data.Sqlite;

namespace MailBot.Core.Storage;

/// <summary>
/// 批次统计仓储：基于 recipients 表聚合"每日成功批次"等历史指标，
/// 供监控页柱状图使用。聚合按 sent_at（UTC 存储）的本地日分组。
/// </summary>
public sealed class BatchStatsRepository
{
    private readonly Database _db;

    public BatchStatsRepository(Database db) => _db = db;

    /// <summary>区间天数上限：超过这个长度视为误操作并拒绝，避免一次拉全表。</summary>
    private const int MaxRangeDays = 366;

    /// <summary>
    /// 返回最近 <paramref name="days"/> 天（含今天）的每日成功批次去重计数。
    /// "成功批次"= status ∈ {sent, verified, unverified, unconfirmed} 且 sent_at 非空 的不同 batch_id。
    /// 按本地日（Asia/Shanghai）分组，缺失天补 0；返回长度 == <paramref name="days"/>，最早 → 最新。
    /// 内部委托给按起止日期的重载。
    /// </summary>
    public IReadOnlyList<DailyBatchCount> GetDailyDistinctBatchCounts(int days)
    {
        if (days <= 0) return Array.Empty<DailyBatchCount>();
        var localToday = DateOnly.FromDateTime(DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        var startLocal = localToday.AddDays(-(days - 1));
        return GetDailyDistinctBatchCounts(startLocal, localToday);
    }

    /// <summary>
    /// 返回 [<paramref name="startLocal"/>, <paramref name="endLocalInclusive"/>] 区间内每天的成功批次去重计数。
    /// 与 <see cref="GetDailyDistinctBatchCounts(int)"/> 同口径；区间长度 = (end - start).Days + 1，
    /// 缺失天补 0，按本地日（Asia/Shanghai）正序返回。
    /// </summary>
    /// <exception cref="ArgumentException">截止早于开始，或区间超过 <see cref="MaxRangeDays"/> 天。</exception>
    public IReadOnlyList<DailyBatchCount> GetDailyDistinctBatchCounts(DateOnly startLocal, DateOnly endLocalInclusive)
    {
        if (endLocalInclusive < startLocal)
            throw new ArgumentException($"截止日期({endLocalInclusive:yyyy-MM-dd})早于开始日期({startLocal:yyyy-MM-dd})。", nameof(endLocalInclusive));
        var length = endLocalInclusive.DayNumber - startLocal.DayNumber + 1;
        if (length > MaxRangeDays)
            throw new ArgumentException($"查询区间超过{MaxRangeDays}天，禁止一次拉全表。", nameof(endLocalInclusive));

        var localToday = DateOnly.FromDateTime(DisplayTimeZone.ToDisplay(DateTime.UtcNow));

        // UTC 取一个更早的安全边界：把本地窗口起点往 UTC 推 1 天，确保本地日 D 对应的 UTC 日都能落入。
        var minUtcDay = startLocal.AddDays(-1)
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var minUtcDayStr = TimeZoneInfo.ConvertTimeToUtc(minUtcDay, DisplayTimeZone.Shanghai)
            .ToString("yyyy-MM-dd");

        var tally = AggregateEligibleBatchIdsByLocalDay(minUtcDayStr, startLocal, endLocalInclusive);

        var result = new List<DailyBatchCount>(length);
        for (var i = 0; i < length; i++)
        {
            var day = startLocal.AddDays(i);
            var count = tally.TryGetValue(day, out var set) ? set.Count : 0;
            result.Add(new DailyBatchCount(day, count));
        }
        return result;
    }

    /// <summary>
    /// 拉 eligible (batch_id, utc_day) 行后按本地日聚合 distinct 批次数；用于"区间"和"汇总"两种查询。
    /// SQL 部分按 UTC 日窗口过滤；本地日的过滤交给 caller。
    /// </summary>
    private Dictionary<DateOnly, HashSet<string>> AggregateEligibleBatchIdsByLocalDay(
        string minUtcDayStr,
        DateOnly windowStartLocal,
        DateOnly windowEndLocal)
    {
        var rawRows = new List<(string BatchId, string UtcDay)>();
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT batch_id, substr(sent_at, 1, 10) AS utc_day
FROM recipients
WHERE sent_at IS NOT NULL
  AND status IN ('sent','verified','unverified','unconfirmed')
  AND batch_id IS NOT NULL
  AND substr(sent_at, 1, 10) >= $minUtcDay
GROUP BY batch_id, utc_day";
            cmd.Parameters.AddWithValue("$minUtcDay", minUtcDayStr);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rawRows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var tally = new Dictionary<DateOnly, HashSet<string>>();
        foreach (var (batchId, utcDayStr) in rawRows)
        {
            var utcDay = DateOnly.Parse(utcDayStr);
            var localDay = DateOnly.FromDateTime(DisplayTimeZone.ToDisplay(
                utcDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
            if (localDay < windowStartLocal || localDay > windowEndLocal) continue;
            if (!tally.TryGetValue(localDay, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                tally[localDay] = set;
            }
            set.Add(batchId);
        }
        return tally;
    }

    /// <summary>
    /// 返回 [<paramref name="startLocal"/>, <paramref name="endLocalInclusive"/>] 区间内每天的"验证通过"邮件数。
    /// 口径：recipients.status = 'verified' 且 verified_at 落在本地日（按 substr(verified_at,1,10) 聚合）。
    /// 缺失天 VerifiedCount=0；按本地日（Asia/Shanghai）正序返回。
    /// </summary>
    /// <exception cref="ArgumentException">截止早于开始，或区间超过 <see cref="MaxRangeDays"/> 天。</exception>
    public IReadOnlyList<DailySendCount> GetDailySendCounts(DateOnly startLocal, DateOnly endLocalInclusive)
    {
        if (endLocalInclusive < startLocal)
            throw new ArgumentException($"截止日期({endLocalInclusive:yyyy-MM-dd})早于开始日期({startLocal:yyyy-MM-dd})。", nameof(endLocalInclusive));
        var length = endLocalInclusive.DayNumber - startLocal.DayNumber + 1;
        if (length > MaxRangeDays)
            throw new ArgumentException($"查询区间超过{MaxRangeDays}天，禁止一次拉全表。", nameof(endLocalInclusive));

        // UTC 安全边界：本地窗口起点往 UTC 推 1 天。
        var minUtcDay = startLocal.AddDays(-1)
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var minUtcDayStr = TimeZoneInfo.ConvertTimeToUtc(minUtcDay, DisplayTimeZone.Shanghai)
            .ToString("yyyy-MM-dd");

        var verifiedByLocal = AggregateVerifiedCountByLocalDay(
            minUtcDayStr: minUtcDayStr,
            windowStartLocal: startLocal,
            windowEndLocal: endLocalInclusive);

        var result = new List<DailySendCount>(length);
        for (var i = 0; i < length; i++)
        {
            var day = startLocal.AddDays(i);
            var count = verifiedByLocal.TryGetValue(day, out var c) ? c : 0;
            result.Add(new DailySendCount(day, count));
        }
        return result;
    }

    /// <summary>
    /// 返回本地今天（Asia/Shanghai）"验证通过"的收件人数。
    /// 口径同 <see cref="GetDailySendCounts"/>：recipients.status='verified' 且 verified_at 落在今天本地日。
    /// 供监控页"今日已发"卡片 + 主窗口顶部"今日已发"显示，避免与统计页"今日"柱口径不一致。
    /// 跨 UTC 日界安全：本地窗口起点 -1 天作为 SQL 下限，
    /// 避免 UTC 0 点附近的 verified_at 行被漏掉；最终按本地日 post-filter。
    /// </summary>
    public int CountTodayVerified()
    {
        var localToday = DateOnly.FromDateTime(DisplayTimeZone.ToDisplay(DateTime.UtcNow));
        var minUtcDay = localToday.AddDays(-1)
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var minUtcDayStr = TimeZoneInfo.ConvertTimeToUtc(minUtcDay, DisplayTimeZone.Shanghai)
            .ToString("yyyy-MM-dd");

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT substr(verified_at, 1, 10) AS utc_day
FROM recipients
WHERE verified_at IS NOT NULL
  AND substr(verified_at, 1, 10) >= $minUtcDay
  AND status = 'verified'";
        cmd.Parameters.AddWithValue("$minUtcDay", minUtcDayStr);
        using var reader = cmd.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var utcDay = DateOnly.Parse(reader.GetString(0));
            var localDay = DateOnly.FromDateTime(DisplayTimeZone.ToDisplay(
                utcDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
            if (localDay == localToday) count++;
        }
        return count;
    }

    /// <summary>
    /// 按 recipients.verified_at 的 UTC 日聚合 status='verified' 的行数，
    /// 再换算成本地日（与 GetDailyDistinctBatchCounts 同口径：UTC→本地日由 C# 单点处理）。
    /// </summary>
    private Dictionary<DateOnly, int> AggregateVerifiedCountByLocalDay(
        string minUtcDayStr,
        DateOnly windowStartLocal,
        DateOnly windowEndLocal)
    {
        var rawRows = new List<(string UtcDay, int Count)>();
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT substr(verified_at, 1, 10) AS utc_day, COUNT(*) AS cnt
FROM recipients
WHERE verified_at IS NOT NULL
  AND substr(verified_at, 1, 10) >= $minUtcDay
  AND status = 'verified'
GROUP BY utc_day";
            cmd.Parameters.AddWithValue("$minUtcDay", minUtcDayStr);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rawRows.Add((reader.GetString(0), reader.GetInt32(1)));
            }
        }

        var tally = new Dictionary<DateOnly, int>();
        foreach (var (utcDayStr, count) in rawRows)
        {
            var utcDay = DateOnly.Parse(utcDayStr);
            var localDay = DateOnly.FromDateTime(DisplayTimeZone.ToDisplay(
                utcDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
            if (localDay < windowStartLocal || localDay > windowEndLocal) continue;
            tally[localDay] = tally.TryGetValue(localDay, out var c) ? c + count : count;
        }
        return tally;
    }

    /// <summary>汇总指标快照：今/本周/本月/累计 distinct batch_id 数，以及最近一次成功发送时间。
    /// 全部从 recipients 表聚合（与 <see cref="GetDailyDistinctBatchCounts"/> 同口径）。</summary>
    public sealed record BatchSummary(
        int Today,
        int ThisWeek,
        int ThisMonth,
        int Total,
        DateTime? LastSentAtUtc);

    /// <summary>
    /// 一次性拉"今/本周/本月/累计"的 distinct batch_id 数 + 最近 sent_at。
    /// 今日 = 本地日 D；本月 = 本地月内；本周 = 本地周一到周日（ISO 周）。
    /// </summary>
    public BatchSummary GetSummary()
    {
        var localNow = DisplayTimeZone.ToDisplay(DateTime.UtcNow);
        var localToday = DateOnly.FromDateTime(localNow);
        var localWeekStart = localToday.AddDays(-(int)localNow.DayOfWeek
            // 周日 = 0 在 .NET 视为本周末；调整为 ISO 周一=0。
            + (localNow.DayOfWeek == DayOfWeek.Sunday ? -6 : 1));
        var localMonthStart = new DateOnly(localToday.Year, localToday.Month, 1);

        // UTC 安全边界：本地窗口起点 - 1 天，确保所有本地日 D 的 UTC 数据都落入。
        var minUtcDay = localMonthStart.AddDays(-1)
            .ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var minUtcDayStr = TimeZoneInfo.ConvertTimeToUtc(minUtcDay, DisplayTimeZone.Shanghai)
            .ToString("yyyy-MM-dd");

        // 拉 eligible 行的 batch_id + utc_day + sent_at。
        var rows = new List<(string BatchId, string UtcDay, DateTime SentAtUtc)>();
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
SELECT batch_id, substr(sent_at, 1, 10) AS utc_day, sent_at
FROM recipients
WHERE sent_at IS NOT NULL
  AND status IN ('sent','verified','unverified','unconfirmed')
  AND batch_id IS NOT NULL
  AND substr(sent_at, 1, 10) >= $minUtcDay";
            cmd.Parameters.AddWithValue("$minUtcDay", minUtcDayStr);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTime.Parse(reader.GetString(2),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind)));
            }
        }

        // 按本地日聚合 → 每天的 distinct batch 集合
        var perLocalDay = new Dictionary<DateOnly, HashSet<string>>();
        // 本周/月/总唯一
        var weekSet = new HashSet<string>(StringComparer.Ordinal);
        var monthSet = new HashSet<string>(StringComparer.Ordinal);
        var totalSet = new HashSet<string>(StringComparer.Ordinal);
        DateTime? lastSent = null;

        foreach (var (batchId, utcDayStr, sentAtUtc) in rows)
        {
            var utcDay = DateOnly.Parse(utcDayStr);
            var localDay = DateOnly.FromDateTime(DisplayTimeZone.ToDisplay(
                utcDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));

            if (!perLocalDay.TryGetValue(localDay, out var daySet))
            {
                daySet = new HashSet<string>(StringComparer.Ordinal);
                perLocalDay[localDay] = daySet;
            }
            daySet.Add(batchId);

            if (localDay >= localWeekStart) weekSet.Add(batchId);
            if (localDay >= localMonthStart) monthSet.Add(batchId);
            totalSet.Add(batchId);

            if (lastSent is null || sentAtUtc > lastSent) lastSent = sentAtUtc;
        }

        var today = perLocalDay.TryGetValue(localToday, out var todaySet) ? todaySet.Count : 0;
        return new BatchSummary(today, weekSet.Count, monthSet.Count, totalSet.Count, lastSent);
    }
}