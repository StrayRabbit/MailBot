using System.IO;
using System.Text;

namespace MailBot.Core.Storage;

/// <summary>
/// 把日志同时写到 &lt;db 同根&gt;/logs/mailbot-yyyy-MM-dd.log。
/// 按天分文件，UTF-8 追加写入。线程安全（lock 串行）。
/// </summary>
public static class LogFileWriter
{
    private static string? _dbPath;
    private static readonly object _lock = new();

    /// <summary>由 <see cref="Database"/> 构造时调用，登记当前 db 路径。</summary>
    public static void TrackDbPath(string dbPath) => _dbPath = dbPath;

    /// <summary>日志目录：&lt;db 父目录&gt;/logs。</summary>
    public static string LogDir
    {
        get
        {
            var dbPath = _dbPath ?? Database.DefaultDbPath();
            return Path.Combine(Path.GetDirectoryName(dbPath)!, "logs");
        }
    }

    /// <summary>
    /// 写入一行日志。失败吞掉异常（不能让日志写入影响主流程）。
    /// 调用方应在外部加 `[HH:mm:ss] xxx` 前缀。
    /// </summary>
    public static void WriteLine(string line)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);
                var path = Path.Combine(LogDir, $"mailbot-{DateTime.Now:yyyy-MM-dd}.log");
                // FileShare.Read 允许外部用文本编辑器实时查看
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(fs, new UTF8Encoding(false));
                writer.WriteLine(line);
            }
        }
        catch
        {
            // 写日志失败不应影响主流程
        }
    }

    /// <summary>
    /// 写入多行块（已包含换行）。整批一次 IO，避免每封一行开关文件。
    /// 调用方控制行格式；不会自动追加尾部换行。
    /// </summary>
    public static void WriteBlock(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);
                var path = Path.Combine(LogDir, $"mailbot-{DateTime.Now:yyyy-MM-dd}.log");
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(fs, new UTF8Encoding(false));
                writer.Write(text);
            }
        }
        catch
        {
            // 写日志失败不应影响主流程
        }
    }
}