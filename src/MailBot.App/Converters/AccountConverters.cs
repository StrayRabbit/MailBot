using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace MailBot.App.Converters;

/// <summary>
/// 把 ISO 时间字符串（yyyy-MM-ddTHH:mm:ss...）格式化为 yyyy-MM-dd HH:mm。
/// 输入为 null / 空 / 解析失败时返回 "—"。
/// </summary>
public sealed class IsoDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrEmpty(s)
            && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.ToString("yyyy-MM-dd HH:mm", culture);
        }
        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 把 Sender 状态字符串映射成颜色：
///   normal  → 绿
///   cooling → 黄
///   banned  → 红
///   其他    → 灰
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            "normal" => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),   // 绿
            "cooling" => new SolidColorBrush(Color.FromRgb(0xE0, 0x9E, 0x08)),  // 黄
            "banned" => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),   // 红
            _ => new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),         // 灰
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 把 Sender 状态字符串映射成中文标签：
///   normal  → "正常"
///   cooling → "冷却"
///   banned  → "封禁"
/// </summary>
public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            "normal" => "正常",
            "cooling" => "冷却",
            "banned" => "封禁",
            _ => value?.ToString() ?? "—",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// bool → 中英对照：true="启用" false="禁用"。用于验证邮箱的 Enabled 列。
/// </summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool b && b ? "启用" : "禁用";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 把 Recipient 状态字符串映射成中文标签（仅 UI 显示，数据库原值不变）：
///   pending     → "待发送"
///   sent        → "已发送"
///   verified    → "已送达"
///   unverified  → "验证中"
///   failed      → "失败"
///   blocked     → "已屏蔽"
///   unconfirmed → "未确认"
///   其他/空     → "—"
/// </summary>
public sealed class RecipientStatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            "pending" => "待发送",
            "sent" => "已发送",
            "verified" => "已送达",
            "unverified" => "验证中",
            "failed" => "失败",
            "blocked" => "已屏蔽",
            "unconfirmed" => "未确认",
            _ => value is null || (value is string s && string.IsNullOrEmpty(s)) ? "—" : value.ToString() ?? "—",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
