using MailBot.Core.Models;
using Microsoft.Data.Sqlite;

namespace MailBot.Core.Storage;

/// <summary>
/// 发件账号仓储。
/// </summary>
public sealed class SenderRepository
{
    private readonly Database _db;

    public SenderRepository(Database db) => _db = db;

    public long Insert(Sender s)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO senders
(email, smtp_host, smtp_port, use_ssl, trust_invalid_certificate, username, password_encrypted,
 daily_quota, sent_today, quota_reset_date, consecutive_failures,
 status, cooldown_until, created_at, updated_at)
VALUES
($email, $host, $port, $ssl, $trust, $user, $pwd,
 $quota, 0, $resetDate, 0,
 $status, NULL, $created, $updated);
SELECT last_insert_rowid();";
        BindSender(cmd, s);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void Update(Sender s)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE senders SET
 smtp_host=$host, smtp_port=$port, use_ssl=$ssl, trust_invalid_certificate=$trust,
 username=$user, password_encrypted=$pwd, daily_quota=$quota, sent_today=$sent,
 quota_reset_date=$resetDate, consecutive_failures=$failures,
 status=$status, cooldown_until=$cooldown, updated_at=$updated
WHERE id=$id";
        BindSender(cmd, s);
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>原子地递增 sent_today 与更新 reset_date（按发送的邮件个数累加）。</summary>
    public void IncrementSentToday(long senderId, int delta, string quotaResetDate)
    {
        if (delta <= 0) return;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE senders
SET sent_today = sent_today + $delta, quota_reset_date = $resetDate, updated_at = $now
WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", senderId);
        cmd.Parameters.AddWithValue("$delta", delta);
        cmd.Parameters.AddWithValue("$resetDate", quotaResetDate);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 退掉已计入的当日配额（verifier SMTP 失败 / IMAP 轮询超时未命中场景）。
    /// SQL 用 MAX(0, …) 保证 sent_today 不会变负。
    /// </summary>
    public void RefundSentToday(long senderId, int count, string quotaResetDate)
    {
        if (count <= 0) return;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE senders
SET sent_today = MAX(0, sent_today - $delta), quota_reset_date = $resetDate, updated_at = $now
WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", senderId);
        cmd.Parameters.AddWithValue("$delta", count);
        cmd.Parameters.AddWithValue("$resetDate", quotaResetDate);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>跨天时把 sent_today 归零，并刷新 quota_reset_date。</summary>
    public void ResetSentToday(long senderId, string quotaResetDate)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE senders
SET sent_today = 0, quota_reset_date = $resetDate, updated_at = $now
WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", senderId);
        cmd.Parameters.AddWithValue("$resetDate", quotaResetDate);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>所有 sender 的 sent_today 总和（跨账号当日总量）。
    /// 供监控页"今日已发"卡片展示；跨天由 ResetSentToday 归零；
    /// 空表返回 0（COALESCE 处理 NULL 聚合）。</summary>
    public int TotalSentToday()
    {
        return ExecuteScalarInt("SELECT COALESCE(SUM(sent_today), 0) FROM senders");
    }

    /// <summary>
    /// 把 sent_today 直接置为指定值（UI 手动调整入口）。负值裁剪到 0。
    /// 同步刷新 quota_reset_date 为今天，避免 ResetQuotaIfNeededAsync 跨天后又重置。
    /// </summary>
    public void SetSentToday(long senderId, int value, string quotaResetDate)
    {
        var clamped = Math.Max(0, value);
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE senders
SET sent_today = $val, quota_reset_date = $resetDate, updated_at = $now
WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", senderId);
        cmd.Parameters.AddWithValue("$val", clamped);
        cmd.Parameters.AddWithValue("$resetDate", quotaResetDate);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>原子地更新状态/连续失败次数/冷却时间。</summary>
    public void UpdateStatus(long senderId, string status, int consecutiveFailures, DateTime? cooldownUntil)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE senders
SET status=$status, consecutive_failures=$failures,
    cooldown_until=$cooldown, updated_at=$now
WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", senderId);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$failures", consecutiveFailures);
        // cooldownUntil 为 null 时必须传 DBNull.Value；直接传 C# null 会让 SqliteParameter.Bind 抛
        // "Value must be set."。BindSender 已经是这个写法，保持一致。
        cmd.Parameters.AddWithValue("$cooldown", (object?)(cooldownUntil?.ToString("o")) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<Sender> GetAll()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM senders ORDER BY id";
        return ReadAll(cmd);
    }

    public Sender? GetById(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM senders WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return ReadOne(cmd);
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM senders WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public int Count() => ExecuteScalarInt("SELECT COUNT(*) FROM senders");

    private int ExecuteScalarInt(string sql)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void BindSender(SqliteCommand cmd, Sender s)
    {
        cmd.Parameters.AddWithValue("$email", s.Email);
        cmd.Parameters.AddWithValue("$host", s.SmtpHost);
        cmd.Parameters.AddWithValue("$port", s.SmtpPort);
        cmd.Parameters.AddWithValue("$ssl", s.UseSsl ? 1 : 0);
        cmd.Parameters.AddWithValue("$trust", s.TrustInvalidCertificate ? 1 : 0);
        cmd.Parameters.AddWithValue("$user", s.Username);
        cmd.Parameters.AddWithValue("$pwd", s.PasswordEncrypted);
        cmd.Parameters.AddWithValue("$quota", s.DailyQuota);
        cmd.Parameters.AddWithValue("$sent", s.SentToday);
        cmd.Parameters.AddWithValue("$resetDate", s.QuotaResetDate);
        cmd.Parameters.AddWithValue("$failures", s.ConsecutiveFailures);
        cmd.Parameters.AddWithValue("$status", s.Status);
        cmd.Parameters.AddWithValue("$cooldown", (object?)s.CooldownUntil ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", s.CreatedAt == default
            ? DateTime.UtcNow.ToString("o")
            : s.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("o"));
    }

    private static Sender? ReadOne(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static List<Sender> ReadAll(SqliteCommand cmd)
    {
        var list = new List<Sender>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    private static Sender Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Email = ReadString(r, "email", ""),
        SmtpHost = ReadString(r, "smtp_host", ""),
        SmtpPort = ReadInt(r, "smtp_port", 465),
        UseSsl = ReadBool(r, "use_ssl", true),
        TrustInvalidCertificate = ReadBool(r, "trust_invalid_certificate", false),
        Username = ReadString(r, "username", ""),
        PasswordEncrypted = r.IsDBNull(r.GetOrdinal("password_encrypted"))
            ? Array.Empty<byte>()
            : (byte[])r.GetValue(r.GetOrdinal("password_encrypted")),
        DailyQuota = ReadInt(r, "daily_quota", 50),
        SentToday = ReadInt(r, "sent_today", 0),
        QuotaResetDate = ReadString(r, "quota_reset_date", ""),
        ConsecutiveFailures = ReadInt(r, "consecutive_failures", 0),
        Status = ReadString(r, "status", "normal"),
        CooldownUntil = r.IsDBNull(r.GetOrdinal("cooldown_until"))
            ? null : r.GetString(r.GetOrdinal("cooldown_until")),
        CreatedAt = ReadDateTime(r, "created_at"),
        UpdatedAt = ReadDateTime(r, "updated_at"),
    };

    // 老库部分行可能存在 NULL 列（schema NOT NULL 约束在老 SQLite 版本上不强制），
    // 对 GetString/GetInt32 加 IsDBNull 兜底，避免 SqliteDataReader 抛 "Value must be set."。
    private static string ReadString(SqliteDataReader r, string col, string dflt)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? dflt : r.GetString(i);
    }

    private static int ReadInt(SqliteDataReader r, string col, int dflt)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? dflt : r.GetInt32(i);
    }

    private static bool ReadBool(SqliteDataReader r, string col, bool dflt)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? dflt : r.GetInt32(i) == 1;
    }

    private static DateTime ReadDateTime(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? DateTime.UtcNow : DateTime.Parse(r.GetString(i));
    }
}