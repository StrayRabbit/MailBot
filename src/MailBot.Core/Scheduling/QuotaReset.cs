namespace MailBot.Core.Scheduling;

/// <summary>
/// 每日配额重置工具。设计文档 5.1/6.2 节：sent_today 在跨过本地午夜时归零。
/// </summary>
public static class QuotaReset
{
    /// <summary>今天是否需要重置：调用方传入 senders 表里存的 quota_reset_date 和当前时间。</summary>
    public static bool NeedsReset(string storedResetDate, DateTime nowLocal)
    {
        if (string.IsNullOrEmpty(storedResetDate)) return true;
        if (!DateOnly.TryParse(storedResetDate, out var stored)) return true;
        return stored < DateOnly.FromDateTime(nowLocal);
    }

    /// <summary>获取今天的 ISO 日期字符串（YYYY-MM-DD）。</summary>
    public static string Today(DateTime nowLocal) =>
        DateOnly.FromDateTime(nowLocal).ToString("yyyy-MM-dd");

    /// <summary>下一次配额重置时刻：次日 00:00 本地时间。</summary>
    public static DateTime NextReset(DateTime nowLocal)
    {
        var tomorrow = nowLocal.Date.AddDays(1);
        return tomorrow;
    }
}