namespace MailBot.Core.Verification;

/// <summary>
/// 批次送达验证的状态。in-memory 状态，Verified / Unconfirmed 是仅有的两个终态；
/// 落库时 Verified → status='verified'，Unconfirmed → 收件人回退 status='pending'（由 worker 处理）。
/// </summary>
public enum BatchState
{
    Pending,
    Verified,
    Unconfirmed,
}