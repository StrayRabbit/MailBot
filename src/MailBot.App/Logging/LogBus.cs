using System.Collections.ObjectModel;
using MailBot.Core.Logging;
using MailBot.Core.Storage;

namespace MailBot.App.Logging;

/// <summary>
/// ILogger 的单例实现。WPF 侧订阅 LogEmitted 拿到 LogEntry；同时把原始字符串行写到日志文件。
/// </summary>
public sealed class LogBus : ILogger
{
    private static readonly Lazy<LogBus> _instance = new(() => new LogBus());
    public static LogBus Instance => _instance.Value;

    /// <summary>UI 绑定的日志集合（MonitorViewModel 直接绑定）。</summary>
    public ObservableCollection<LogEntry> Logs { get; } = new();

    /// <summary>新条目事件——订阅者（MainViewModel）负责切到 UI 线程后入列。</summary>
    public event EventHandler<LogEntry>? LogEmitted;

    private LogBus() { }

    public void Emit(LogLevel level, string message, string? account = null)
    {
        var entry = new LogEntry(DateTime.Now, level, message, account);
        LogEmitted?.Invoke(this, entry);
        // 双写文件（带 HH:mm:ss + 级别符号，供排查）。原 Logged 事件也写文件——保留以便不改 Core 日志逻辑。
        LogFileWriter.WriteLine(FormatForFile(entry));
    }

    /// <summary>恢复测试——清空事件订阅和集合。</summary>
    internal void ResetForTest()
    {
        LogEmitted = null;
        Logs.Clear();
    }

    private static string FormatForFile(LogEntry e)
    {
        var prefix = e.Level switch
        {
            LogLevel.Success => "[成功]",
            LogLevel.Pending => "[等待验证]",
            LogLevel.Warning => "[警告]",
            LogLevel.Error => "[失败]",
            _ => "[信息]",
        };
        var acct = string.IsNullOrEmpty(e.Account) ? "" : $" {e.Account}";
        return $"[{e.Timestamp:HH:mm:ss}] {prefix}{acct} {e.Message}";
    }
}