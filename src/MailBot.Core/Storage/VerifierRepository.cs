using MailBot.Core.Models;
using Microsoft.Data.Sqlite;

namespace MailBot.Core.Storage;

/// <summary>
/// 验证邮箱仓储。结构与 SenderRepository 平行：参数化 SQL、DBNull.Value 兜底。
/// </summary>
public sealed class VerifierRepository
{
    private readonly Database _db;

    public VerifierRepository(Database db) => _db = db;

    public long Insert(VerifierAccount v)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO verifier_accounts
(email, imap_host, imap_port, use_ssl, username, password_encrypted,
 enabled, last_polled_at, last_seen_uid, created_at, updated_at)
VALUES
($email, $host, $port, $ssl, $user, $pwd,
 $enabled, NULL, NULL, $created, $updated);
SELECT last_insert_rowid();";
        Bind(cmd, v);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void Update(VerifierAccount v)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE verifier_accounts SET
 imap_host=$host, imap_port=$port, use_ssl=$ssl, username=$user, password_encrypted=$pwd,
 enabled=$enabled, updated_at=$updated
WHERE id=$id";
        Bind(cmd, v);
        cmd.Parameters.AddWithValue("$id", v.Id);
        cmd.ExecuteNonQuery();
    }

    public void UpdateLastSeen(long id, uint uid)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE verifier_accounts
SET last_seen_uid=$uid, last_polled_at=$now, updated_at=$now
WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$uid", (long)uid);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把水位线重置为 0（下次从头拉）。用于水位线漂移（last_seen_uid &gt; 收件箱实际最大 UID）导致永远拉不到新邮件。
    /// </summary>
    public void ResetLastSeen(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE verifier_accounts SET last_seen_uid=0, updated_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<VerifierAccount> GetAll()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM verifier_accounts ORDER BY id";
        return ReadAll(cmd);
    }

    public List<VerifierAccount> ListEnabled()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM verifier_accounts WHERE enabled=1 ORDER BY id";
        return ReadAll(cmd);
    }

    public VerifierAccount? GetById(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM verifier_accounts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public VerifierAccount? FindByEmail(string email)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM verifier_accounts WHERE email=$email";
        cmd.Parameters.AddWithValue("$email", email);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Delete(long id)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM verifier_accounts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, VerifierAccount v)
    {
        cmd.Parameters.AddWithValue("$email", v.Email);
        cmd.Parameters.AddWithValue("$host", v.ImapHost);
        cmd.Parameters.AddWithValue("$port", v.ImapPort);
        cmd.Parameters.AddWithValue("$ssl", v.UseSsl ? 1 : 0);
        cmd.Parameters.AddWithValue("$user", v.Username);
        cmd.Parameters.AddWithValue("$pwd", v.PasswordEncrypted);
        cmd.Parameters.AddWithValue("$enabled", v.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", v.CreatedAt == default
            ? DateTime.UtcNow.ToString("o")
            : v.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("o"));
    }

    private static List<VerifierAccount> ReadAll(SqliteCommand cmd)
    {
        var list = new List<VerifierAccount>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    private static VerifierAccount Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Email = ReadString(r, "email", ""),
        ImapHost = ReadString(r, "imap_host", ""),
        ImapPort = ReadInt(r, "imap_port", 993),
        UseSsl = ReadBool(r, "use_ssl", true),
        Username = ReadString(r, "username", ""),
        PasswordEncrypted = r.IsDBNull(r.GetOrdinal("password_encrypted"))
            ? Array.Empty<byte>()
            : (byte[])r.GetValue(r.GetOrdinal("password_encrypted")),
        Enabled = ReadBool(r, "enabled", true),
        LastPolledAt = r.IsDBNull(r.GetOrdinal("last_polled_at"))
            ? null : DateTime.Parse(r.GetString(r.GetOrdinal("last_polled_at"))),
        LastSeenUid = r.IsDBNull(r.GetOrdinal("last_seen_uid"))
            ? null : (uint)r.GetInt64(r.GetOrdinal("last_seen_uid")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("created_at"))),
        UpdatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("updated_at"))),
    };

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
}