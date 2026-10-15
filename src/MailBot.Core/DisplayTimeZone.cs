namespace MailBot.Core;

/// <summary>
/// UI 显示用的时区工具：所有"给用户看的时间戳"统一转 Asia/Shanghai（东八区），
/// 不依赖宿主操作系统 TZ。这样把 MailBot 部署到 UTC 服务器上，监控面板仍然显示北京时间。
/// 数据库写入仍用 UTC（见 *UtcNow 调用点），不变。
/// </summary>
public static class DisplayTimeZone
{
    /// <summary>UI 显示时区，硬编码 Asia/Shanghai（东八区）。</summary>
    public static TimeZoneInfo Shanghai { get; } =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    /// <summary>把 UTC DateTime 转成 UI 显示用的本地时间（东八区）。</summary>
    public static DateTime ToDisplay(DateTime utc)
    {
        var asUtc = utc.Kind == DateTimeKind.Utc
            ? utc
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(asUtc, Shanghai);
    }

    /// <summary>把 UTC DateTimeOffset 转成 UI 显示用的本地时间（东八区）。</summary>
    public static DateTime ToDisplay(DateTimeOffset utc)
        => TimeZoneInfo.ConvertTime(utc, Shanghai).DateTime;
}