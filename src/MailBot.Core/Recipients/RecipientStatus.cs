namespace MailBot.Core.Recipients;

/// <summary>
/// 收件人状态常量。取值为 recipients.status 列的字符串，UI 与仓储都以此为准。
/// </summary>
public static class RecipientStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Verified = "verified";
    public const string Unverified = "unverified";
    public const string Failed = "failed";
    public const string Blocked = "blocked";
    public const string Unconfirmed = "unconfirmed";

    /// <summary>全部合法状态，供编辑对话框的下拉框取用（顺序即下拉框显示顺序）。</summary>
    public static readonly string[] All =
    {
        Pending, Sent, Verified, Unverified, Failed, Blocked, Unconfirmed,
    };
}