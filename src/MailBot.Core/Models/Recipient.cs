namespace MailBot.Core.Models;

/// <summary>
/// 收件人。
/// </summary>
public sealed class Recipient
{
    public long Id { get; set; }
    public string Email { get; set; } = string.Empty;

    /// <summary>pending / sent / failed / blocked。</summary>
    public string Status { get; set; } = "pending";

    public string? ErrorMessage { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public long? SentViaSenderId { get; set; }
    public DateTime ImportedAt { get; set; }
    public string? SourceFile { get; set; }

    /// <summary>批次 ID（送达验证用）：同一封 BCC 邮件的所有收件人共享。</summary>
    public string? BatchId { get; set; }

    /// <summary>送达验证通过的时间戳（IMAP 命中或乐观确认）。</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>PickPending 过滤条件：NULL=随时可发；非 NULL 表示该时刻之前跳过该行（冷却中）。</summary>
    public DateTime? PendingRetryAt { get; set; }

    /// <summary>该收件人累计被 unconfirmed 的次数。batch 粒度取 max = 批次当前累计轮数。</summary>
    public int UnconfirmedCount { get; set; }
}