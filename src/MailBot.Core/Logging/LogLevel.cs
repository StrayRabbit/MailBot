namespace MailBot.Core.Logging;

/// <summary>
/// 日志严重级别。驱动 UI 符号与颜色（LogBus 内部 switch 转换）。
/// </summary>
public enum LogLevel
{
    /// <summary>例行检查、重试、冷启动。灰色。</summary>
    Info,

    /// <summary>等待验证中。蓝色。</summary>
    Pending,

    /// <summary>发送成功、验证命中。绿色。</summary>
    Success,

    /// <summary>账号冷却、封禁、验证超时、verifier 发送失败。琥珀色。</summary>
    Warning,

    /// <summary>发送失败、worker 异常、单条出队。红色。</summary>
    Error,
}