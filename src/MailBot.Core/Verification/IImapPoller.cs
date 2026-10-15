using MailBot.Core.Models;

namespace MailBot.Core.Verification;

/// <summary>
/// IMAP 拉取抽象。Worker 不直接依赖 MailKit，便于单测用 fake 替换。
/// </summary>
public interface IImapPoller
{
    /// <summary>
    /// 按时间窗拉取验证邮箱的邮件元数据：返回 receivedSince 之后的所有邮件。
    /// 不依赖水位线，每次都按时间窗重新扫描；重复扫描由调用方按"批次状态机"去重（Pending 才参与命中判定）。
    /// 返回 (fromEmail, uid, internalDate) 元组列表。
    /// **此方法永不抛出**——所有异常在内部捕获并记日志，返回空集合。
    /// </summary>
    Task<IReadOnlyList<(string FromEmail, uint Uid, DateTimeOffset? InternalDate)>>
        PollSinceAsync(
            VerifierAccount account,
            DateTimeOffset receivedSince,
            CancellationToken ct);

    /// <summary>仅做连接测试，不拉邮件。供 UI "测试连接" 按钮使用。</summary>
    Task<(bool ok, string? error)> TestConnectionAsync(
        VerifierAccount account,
        CancellationToken ct);

    /// <summary>
    /// 仅返回收件箱当前最大 UID（用于水位线漂移检测）。
    /// 连接失败返回 null，不抛异常。
    /// </summary>
    Task<uint?> GetInboxMaxUidAsync(
        VerifierAccount account,
        CancellationToken ct);
}