using MailBot.App.Logging;
using MailBot.Core.Logging;
using MailBot.Core.Storage;
using Xunit;

namespace MailBot.Tests.Logging;

// 共享 LogBus 单例：所有 LogBus 相关测试串行运行，避免并发测试相互污染 _emits / 事件订阅。
[Collection("LogBus")]
public class LogBusTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;

    public LogBusTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_logbus_{Guid.NewGuid():N}.db");
        _dbPath = Path.ChangeExtension(_dbPath, ".db");
        // 调整数据库路径后通知 LogFileWriter 跟随
        _db = new Database(_dbPath);
        _db.EnsureSchema();
    }

    public void Dispose()
    {
        // 清空实例状态（事件+Logs）便于下一个测试
        LogBus.Instance.ResetForTest();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        var logDir = Path.Combine(Path.GetDirectoryName(_dbPath)!, "logs");
        if (Directory.Exists(logDir)) Directory.Delete(logDir, recursive: true);
    }

    [Fact]
    public void Emit_AddsEntryToLogsAndFiresEvent()
    {
        LogEntry? captured = null;
        LogBus.Instance.LogEmitted += (_, e) => captured = e;

        LogBus.Instance.Emit(LogLevel.Success, "已验证 (…7bff)，用时 139s");

        Assert.NotNull(captured);
        Assert.Equal(LogLevel.Success, captured!.Level);
        Assert.Equal("已验证 (…7bff)，用时 139s", captured.Message);
        // Emit 只触发事件、不直接入 Logs；Logs 入列由订阅者负责。
        Assert.Empty(LogBus.Instance.Logs);
    }

    [Fact]
    public void Emit_WithAccount_PassesThroughToEntry()
    {
        LogEntry? captured = null;
        LogBus.Instance.LogEmitted += (_, e) => captured = e;

        LogBus.Instance.Emit(LogLevel.Warning, "验证超时 (…8f21)，冷却 60min", account: "lsc@x.com");

        Assert.Equal("lsc@x.com", captured!.Account);
    }

    [Fact]
    public void Emit_WritesLineToLogFile()
    {
        LogBus.Instance.Emit(LogLevel.Error, "发送失败：SMTP 421");

        var logPath = Path.Combine(
            Path.GetDirectoryName(_dbPath)!,
            "logs",
            $"mailbot-{DateTime.Now:yyyy-MM-dd}.log");
        Assert.True(File.Exists(logPath), $"日志文件应存在: {logPath}");
        var content = File.ReadAllText(logPath);
        Assert.Contains("[失败]", content);
        Assert.Contains("发送失败：SMTP 421", content);
    }
}