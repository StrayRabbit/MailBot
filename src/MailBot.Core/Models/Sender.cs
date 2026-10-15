namespace MailBot.Core.Models;

/// <summary>
/// 发件账号。
/// </summary>
public sealed class Sender
{
    public long Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 465;
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// 是否跳过 SMTP 服务器证书校验（主机名不匹配、自签名等）。
    /// 仅用于自建/虚拟空间邮箱等无法获得合法证书的场景。默认 false。
    /// </summary>
    public bool TrustInvalidCertificate { get; set; }

    public string Username { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的密码字节。</summary>
    public byte[] PasswordEncrypted { get; set; } = Array.Empty<byte>();

    public int DailyQuota { get; set; } = 50;
    public int SentToday { get; set; }
    public string QuotaResetDate { get; set; } = string.Empty;
    public int ConsecutiveFailures { get; set; }
    public string Status { get; set; } = "normal";

    /// <summary>ISO 时间（含时区），冷却到期时间。</summary>
    public string? CooldownUntil { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}