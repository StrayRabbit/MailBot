namespace MailBot.Core.Models;

/// <summary>
/// 验证邮箱：用于接收 BCC 抄送的送达验证邮件。
/// 与发件账号 Sender 同构（DPAPI 加密凭据），但用 IMAP 拉取而非 SMTP 发送。
/// </summary>
public sealed class VerifierAccount
{
    public long Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string ImapHost { get; set; } = string.Empty;
    public int ImapPort { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的凭据字节。QQ 邮箱此处存的是授权码，不是登录密码。</summary>
    public byte[] PasswordEncrypted { get; set; } = Array.Empty<byte>();

    public bool Enabled { get; set; } = true;
    public DateTime? LastPolledAt { get; set; }
    public uint? LastSeenUid { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}