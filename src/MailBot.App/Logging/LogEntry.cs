// WPF + WinForms 共存项目：Brush / Brushes / Color 在两命名空间都存在，
// 用别名锁定为 WPF 版本，参考 AccountConverters.cs 的做法。
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using System.Windows.Media;
using MailBot.Core.Logging;

namespace MailBot.App.Logging;

/// <summary>
/// UI 可见的单条日志。WPF 绑定 TimestampText/Symbol/LevelBrush 渲染。
/// </summary>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message, string? Account = null)
{
    /// <summary>"HH:mm" 精度——后台长跑秒级意义不大。</summary>
    public string TimestampText => Timestamp.ToString("HH:mm");

    /// <summary>状态符号。集中在此，避免模板里写转换器。</summary>
    public string Symbol => Level switch
    {
        LogLevel.Success => "✓",
        LogLevel.Pending => "⏳",
        LogLevel.Warning => "⚠",
        LogLevel.Error => "✗",
        _ => "·",
    };

    /// <summary>柔色系：长时间盯屏不刺眼。</summary>
    public Brush LevelBrush => Level switch
    {
        LogLevel.Success => Brushes.MediumSeaGreen,           // #3CB371
        LogLevel.Pending => Brushes.SteelBlue,                // #4682B4
        LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xF2, 0xB3, 0x6B)),
        LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xF2, 0x80, 0x80)),
        _ => Brushes.DarkGray,
    };
}