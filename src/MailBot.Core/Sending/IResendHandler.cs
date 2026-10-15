using MailBot.Core.Verification;

namespace MailBot.Core.Sending;

/// <summary>
/// "换 verifier 重发整批 BCC" 的能力抽象。从 SendEngine 抽出是为了让 VerificationWorker
/// 测试能用 FakeResendHandler 直接控制成功失败、而不需要构造整个 SMTP 栈。
/// </summary>
public interface IResendHandler
{
    /// <summary>
    /// 对指定 BatchTracker，用 newVerifierId 作为新的 BCC verifier，重发整批邮件。
    /// 返回 true = SMTP 成功且 BatchTracker 状态已更新（AttemptedVerifierIds / VerifierRetryCount / ResentAt）；
    /// 返回 false = 应放弃重发，由调用方走 Unconfirmed 路径。
    /// </summary>
    Task<bool> ResendBatchAsync(BatchTracker tracker, long newVerifierId, CancellationToken ct);
}