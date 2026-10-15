namespace MailBot.Core.Logging;

/// <summary>
/// Core 层向 UI 暴露的精简日志通道。与现有 SendEngine.Logged 事件 / VerificationWorker._log
/// 委托并行——后者保留走文件路径。Emit 走 UI 显示路径，由 App 层 LogBus 实现。
/// 调用方传 message 时应已包含账号（如果该条需要账号定位）。
/// </summary>
public interface ILogger
{
    /// <summary>
    /// 发射一条 UI 可见日志。
    /// </summary>
    /// <param name="level">级别，决定 UI 符号/颜色。</param>
    /// <param name="message">精简后的中文文本。不需要带时间戳、batch 短码已是完整 ID 之外的短形式。</param>
    /// <param name="account">需要附账号邮箱的行才传；例行行不传。null 表示无账号。</param>
    void Emit(LogLevel level, string message, string? account = null);
}