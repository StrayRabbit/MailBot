using MailBot.Core.Models;
using Microsoft.Data.Sqlite;

namespace MailBot.Core.Storage;

/// <summary>
/// <see cref="RecipientRepository.Update"/> 的结果。
/// </summary>
public enum UpdateRecipientResult
{
    Success,
    NotFound,
    DuplicateEmail,
}

/// <summary>
/// 收件人仓储。负责批量导入、状态推进、结果回写与按状态统计。
/// </summary>
public sealed class RecipientRepository
{
    private readonly Database _db;

    public RecipientRepository(Database db) => _db = db;

    /// <summary>批量导入收件人；同一 (email, source_file) 视为重复，跳过。返回实际新增数量。</summary>
    public int InsertBatch(IEnumerable<Recipient> recipients, string? sourceFile)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT OR IGNORE INTO recipients
(email, status, attempt_count, imported_at, source_file)
VALUES ($email, 'pending', 0, $imported, $source)";
        var importedAt = DateTime.UtcNow.ToString("o");
        var pEmail = cmd.Parameters.Add("$email", SqliteType.Text);
        var pImported = cmd.Parameters.Add("$imported", SqliteType.Text);
        var pSource = cmd.Parameters.Add("$source", SqliteType.Text);

        int inserted = 0;
        foreach (var r in recipients)
        {
            pEmail.Value = r.Email;
            pImported.Value = importedAt;
            pSource.Value = (object?)sourceFile ?? DBNull.Value;
            inserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return inserted;
    }

    /// <summary>标记整批为 unverified（SMTP 250 OK 但验证邮箱尚未确认）。</summary>
    public void MarkUnverified(IEnumerable<long> recipientIds, string batchId)
    {
        var ids = recipientIds.ToList();
        if (ids.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
UPDATE recipients
SET status='unverified', batch_id=$bid, verified_at=NULL,
    last_attempt_at=$now
WHERE id=$id";
        cmd.Parameters.Add("$bid", SqliteType.Text);
        cmd.Parameters.Add("$now", SqliteType.Text);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var now = DateTime.UtcNow.ToString("o");
        foreach (var id in ids)
        {
            cmd.Parameters["$bid"].Value = batchId;
            cmd.Parameters["$now"].Value = now;
            pId.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// 无验证模式：SMTP 成功后按 id 直接标 verified（"默认校验通过"）。
    /// 不分配 batch_id，所以走不到 <see cref="MarkVerified"/> 那条按批次号匹配的路径。
    /// 同时补齐 sent_at / sent_via_sender_id / attempt_count，让"最后活动"列和
    /// 有验证模式的行保持一致，不至于因缺字段被 UI 显示成"从未尝试过"。
    /// </summary>
    public void MarkVerifiedWithoutVerification(IEnumerable<long> recipientIds, long senderId, DateTime verifiedAt)
    {
        var ids = recipientIds.ToList();
        if (ids.Count == 0) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
UPDATE recipients
SET status='verified', verified_at=$at, sent_at=$at, sent_via_sender_id=$sid,
    error_message=NULL, attempt_count = attempt_count + 1, last_attempt_at=$at
WHERE id=$id";
        cmd.Parameters.Add("$at", SqliteType.Text);
        cmd.Parameters.Add("$sid", SqliteType.Integer);
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        var at = verifiedAt.ToString("o");
        foreach (var id in ids)
        {
            cmd.Parameters["$at"].Value = at;
            cmd.Parameters["$sid"].Value = senderId;
            pId.Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>标记整批为 verified（IMAP 命中）。</summary>
    public void MarkVerified(string batchId, DateTime verifiedAt)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE recipients
SET status='verified', verified_at=$at
WHERE batch_id=$bid AND status='unverified'";
        cmd.Parameters.AddWithValue("$at", verifiedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$bid", batchId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>标记整批为 unconfirmed（验证邮箱超时未收到）。</summary>
    public void MarkUnconfirmed(string batchId, string reason)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE recipients
SET status='unconfirmed', error_message=$err, verified_at=$at
WHERE batch_id=$bid AND status='unverified'";
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$bid", batchId);
        cmd.Parameters.AddWithValue("$err", reason);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把整批标 unconfirmed，同时给每个收件人写 pending_retry_at = now + cooldown,
    /// 并累加 unconfirmed_count。事务保证三件事要么都做要么都不做。
    /// 两步 UPDATE 目的：先标 unconfirmed（从 unverified 状态），再兜底写已经在 pending 状态的同批次行。
    /// </summary>
    public void MarkUnconfirmedWithCooldown(string batchId, string reason, TimeSpan cooldown)
    {
        if (string.IsNullOrEmpty(batchId)) return;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        var now = DateTime.UtcNow;
        var retryAt = now.Add(cooldown).ToString("o");

        // (1) 把当前 unverified 的标 unconfirmed，同时写冷却 + 累加计数
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE recipients
SET status='unconfirmed', error_message=$err, verified_at=$at,
    pending_retry_at=$retry, unconfirmed_count = unconfirmed_count + 1
WHERE batch_id=$bid AND status='unverified'";
            cmd.Parameters.AddWithValue("$err", reason);
            cmd.Parameters.AddWithValue("$at", now.ToString("o"));
            cmd.Parameters.AddWithValue("$retry", retryAt);
            cmd.Parameters.AddWithValue("$bid", batchId);
            cmd.ExecuteNonQuery();
        }

        // (2) 兜底：已 reset 回 pending 的同批次行，补写冷却（防住"unconfirmed→reset"的中间缝）
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE recipients
SET pending_retry_at=$retry
WHERE batch_id=$bid AND status='pending' AND pending_retry_at IS NULL";
            cmd.Parameters.AddWithValue("$retry", retryAt);
            cmd.Parameters.AddWithValue("$bid", batchId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// 从 pending 中随机取最多 batchSize 条，且过滤冷却中的行（pending_retry_at > now）。
    /// 不主动过滤 unconfirmed：unconfirmed 是终态（等人工 ResetToPending 后才会重发），不会污染 pending。
    /// </summary>
    public List<Recipient> PickPending(int batchSize)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT * FROM recipients
WHERE status = 'pending'
  AND (pending_retry_at IS NULL OR pending_retry_at <= $now)
ORDER BY RANDOM()
LIMIT $limit";
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$limit", batchSize);
        return ReadAll(cmd);
    }

    public void MarkSent(long recipientId, long senderId)
    {
        UpdateStatus(recipientId, "sent", null, senderId, markSent: true);
    }

    public void MarkFailed(long recipientId, string errorMessage)
    {
        UpdateStatus(recipientId, "failed", errorMessage, senderId: null, markSent: false);
    }

    public void IncrementAttempt(long recipientId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE recipients
SET attempt_count = attempt_count + 1, last_attempt_at = $now
WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", recipientId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 批量递增 attempt_count 并返回每个 id 递增后的新值。
    /// 返回字典：未命中的 id 不出现（理论上不会出现，调用方一般手里就是已存在的 id）。
    /// </summary>
    public Dictionary<long, int> IncrementAttemptsAndReturnCounts(IEnumerable<long> recipientIds)
    {
        var result = new Dictionary<long, int>();
        var ids = recipientIds.ToList();
        if (ids.Count == 0) return result;

        var now = DateTime.UtcNow.ToString("o");
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = @"
UPDATE recipients
SET attempt_count = attempt_count + 1, last_attempt_at = $now
WHERE id = $id";
            upd.Parameters.Add("$now", SqliteType.Text);
            upd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in ids)
            {
                upd.Parameters["$now"].Value = now;
                upd.Parameters["$id"].Value = id;
                upd.ExecuteNonQuery();
            }
        }

        // 读回每个 id 的当前 attempt_count。用 IN 子句一次查，避免 N 次往返。
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            // SQLite IN(?) 不支持集合展开，用临时表占位；这里直接拼接参数，
            // 调用方传入的 ids 已经是 long，不会被注入。
            var placeholders = string.Join(",", ids.Select((_, i) => $"$id{i}"));
            sel.CommandText = $"SELECT id, attempt_count FROM recipients WHERE id IN ({placeholders})";
            for (int i = 0; i < ids.Count; i++)
            {
                sel.Parameters.AddWithValue($"$id{i}", ids[i]);
            }
            using var reader = sel.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetInt64(0)] = reader.GetInt32(1);
            }
        }

        tx.Commit();
        return result;
    }

    private void UpdateStatus(long recipientId, string status, string? errorMessage, long? senderId, bool markSent)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = markSent
            ? @"UPDATE recipients
SET status=$status, sent_at=$now, sent_via_sender_id=$sid,
    error_message=NULL, attempt_count = attempt_count + 1, last_attempt_at=$now
WHERE id=$id"
            : @"UPDATE recipients
SET status=$status, error_message=$err,
    attempt_count = attempt_count + 1, last_attempt_at=$now
WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", recipientId);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        if (markSent)
        {
            cmd.Parameters.AddWithValue("$sid", (object?)senderId ?? DBNull.Value);
        }
        else
        {
            cmd.Parameters.AddWithValue("$err", (object?)errorMessage ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }

    public int CountByStatus(string status) => ExecuteScalarInt(
        "SELECT COUNT(*) FROM recipients WHERE status = $s",
        p => p.AddWithValue("$s", status));

    public List<Recipient> GetByStatus(string status, int limit = 100)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM recipients WHERE status = $s ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    /// <summary>
    /// 按 id 集合查回收件人。供 SendEngine.ResendBatchAsync 重发整批用——按 tracker.RecipientIds 复原原始收件人列表。
    /// 空集合直接返回空列表；重复 id 去重。结果按 id 升序保持稳定顺序。
    /// </summary>
    public List<Recipient> GetByIds(IEnumerable<long> ids)
    {
        var idList = ids?.Distinct().ToList() ?? new List<long>();
        if (idList.Count == 0) return new List<Recipient>();

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var paramNames = new List<string>(idList.Count);
        for (int i = 0; i < idList.Count; i++)
        {
            var name = $"$id{i}";
            paramNames.Add(name);
            cmd.Parameters.AddWithValue(name, idList[i]);
        }
        cmd.CommandText = $"SELECT * FROM recipients WHERE id IN ({string.Join(",", paramNames)}) ORDER BY id";
        return ReadAll(cmd);
    }

    /// <summary>分页获取所有收件人（用于 UI 分页）。</summary>
    public List<Recipient> GetPaged(int offset, int limit)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM recipients ORDER BY id LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return ReadAll(cmd);
    }

    /// <summary>按状态分页获取收件人。</summary>
    public List<Recipient> GetPagedByStatus(string status, int offset, int limit)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM recipients WHERE status = $s ORDER BY id DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return ReadAll(cmd);
    }

    public List<Recipient> GetAll(int limit = 10000)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM recipients ORDER BY id LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    public int Count() => ExecuteScalarInt("SELECT COUNT(*) FROM recipients");

    /// <summary>收件人查询条件。任一字段 null/空 = 不过滤该字段。Status="all" 等价于 null。</summary>
    public sealed record QuerySpec(string? Status, string? EmailLike, string? BatchId);

    /// <summary>按条件分页查询收件人。结果按 id DESC 稳定排序，与现有 GetPaged 行为一致。</summary>
    public List<Recipient> Query(QuerySpec spec, int offset, int limit)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        var where = BuildWhere(spec, cmd.Parameters);
        cmd.CommandText = $"SELECT * FROM recipients{where} ORDER BY id DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        return ReadAll(cmd);
    }

    /// <summary>按条件统计总数（用于分页）。</summary>
    public int Count(QuerySpec spec)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM recipients{BuildWhere(spec, cmd.Parameters)}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 拼接 WHERE 子句和参数。任一条件为 null/空 = 不加该子句。
    /// - email 列导入时已统一小写，LIKE 比较天然大小写不敏感（ASCII），无需 LOWER()。
    /// - 批次号精确匹配：避免 "29" 误中 "2026-09-29-001-a3f4" 等子串场景。
    /// </summary>
    private static string BuildWhere(QuerySpec s, SqliteParameterCollection p)
    {
        var clauses = new List<string>();
        if (!string.IsNullOrEmpty(s.Status) && s.Status != "all")
        {
            clauses.Add("status = $status");
            p.AddWithValue("$status", s.Status);
        }
        if (!string.IsNullOrWhiteSpace(s.EmailLike))
        {
            clauses.Add("email LIKE $email");
            p.AddWithValue("$email", "%" + s.EmailLike.Trim() + "%");
        }
        if (!string.IsNullOrWhiteSpace(s.BatchId))
        {
            clauses.Add("batch_id = $bid");
            p.AddWithValue("$bid", s.BatchId.Trim());
        }
        return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
    }

    /// <summary>
    /// 更新单条收件人的邮箱与状态。
    /// 表上有 UNIQUE(email, source_file)，所以判重必须带上 source_file 才等价于唯一约束；
    /// 查重与更新在同一事务里完成，避免并发下两个窗口同时改成同一个邮箱。
    /// </summary>
    public UpdateRecipientResult Update(long id, string email, string status, string? sourceFile)
    {
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        cmd.CommandText = @"
SELECT COUNT(1) FROM recipients
WHERE email = $email
  AND source_file IS $source
  AND id <> $id";
        cmd.Parameters.AddWithValue("$email", email);
        cmd.Parameters.AddWithValue("$source", (object?)sourceFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt64(cmd.ExecuteScalar()) > 0)
        {
            tx.Rollback();
            return UpdateRecipientResult.DuplicateEmail;
        }

        cmd.CommandText = "UPDATE recipients SET email = $email, status = $status WHERE id = $id";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$email", email);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$id", id);
        int affected = cmd.ExecuteNonQuery();
        if (affected == 0)
        {
            tx.Rollback();
            return UpdateRecipientResult.NotFound;
        }

        tx.Commit();
        return UpdateRecipientResult.Success;
    }

    /// <summary>按 id 删除一批收件人。返回实际删除的收件人行数。</summary>
    public int Delete(IEnumerable<long> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return 0;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM recipients WHERE id = $id";
        var p = cmd.Parameters.Add("$id", SqliteType.Integer);
        int total = 0;
        foreach (var id in idList)
        {
            p.Value = id;
            total += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return total;
    }

    /// <summary>
    /// 清空整张收件人表，不受筛选、分页、勾选影响。返回实际删除的行数。
    /// 调用方必须已向用户二次确认——此操作不可恢复。
    /// </summary>
    public int DeleteAll()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM recipients";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把指定 id 的收件人重置为 pending(不判断当前状态),
    /// 同时清空 sent_at / sent_via_sender_id / error_message / last_attempt_at,
    /// 并将 attempt_count 清零,便于引擎下次重新捞起来发送。
    /// 返回实际发生 UPDATE 的行数。
    /// </summary>
    public int ResetToPending(IEnumerable<long> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return 0;
        using var conn = _db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"UPDATE recipients
SET status='pending', sent_at=NULL, sent_via_sender_id=NULL,
    error_message=NULL, last_attempt_at=NULL, attempt_count=0
WHERE id = $id";
        var p = cmd.Parameters.Add("$id", SqliteType.Integer);
        int total = 0;
        foreach (var id in idList)
        {
            p.Value = id;
            total += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return total;
    }

    /// <summary>
    /// 把指定 batch_id 的所有收件人重置为 pending(不判断当前状态),
    /// 同时清空 sent_at / sent_via_sender_id / error_message / last_attempt_at,
    /// 并将 attempt_count 清零。返回实际发生 UPDATE 的行数。
    /// </summary>
    public int ResetToPendingByBatchId(string batchId)
    {
        if (string.IsNullOrEmpty(batchId)) return 0;
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE recipients
SET status='pending', sent_at=NULL, sent_via_sender_id=NULL,
    error_message=NULL, last_attempt_at=NULL, attempt_count=0
WHERE batch_id = $bid";
        cmd.Parameters.AddWithValue("$bid", batchId);
        return cmd.ExecuteNonQuery();
    }

    private int ExecuteScalarInt(string sql, Action<SqliteParameterCollection>? setup = null)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        setup?.Invoke(cmd.Parameters);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<Recipient> ReadAll(SqliteCommand cmd)
    {
        var list = new List<Recipient>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    /// <summary>
    /// 把 ISO-8601 字符串解析为 UTC Kind 的 DateTime。
    /// 写入端固定用 <c>DateTime.UtcNow.ToString("o")</c>，输出 Z 后缀，
    /// 但旧的 "2026-09-27T18:36:53+08:00" 形式（无 Z）也需要正确解读为同一时刻。
    /// </summary>
    private static DateTime ParseUtc(string s)
    {
        var dt = DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        };
    }

    private static Recipient Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Email = r.GetString(r.GetOrdinal("email")),
        Status = r.GetString(r.GetOrdinal("status")),
        ErrorMessage = r.IsDBNull(r.GetOrdinal("error_message"))
            ? null : r.GetString(r.GetOrdinal("error_message")),
        AttemptCount = r.GetInt32(r.GetOrdinal("attempt_count")),
        LastAttemptAt = r.IsDBNull(r.GetOrdinal("last_attempt_at"))
            ? null : DateTime.Parse(r.GetString(r.GetOrdinal("last_attempt_at"))),
        SentAt = r.IsDBNull(r.GetOrdinal("sent_at"))
            ? null : DateTime.Parse(r.GetString(r.GetOrdinal("sent_at"))),
        SentViaSenderId = r.IsDBNull(r.GetOrdinal("sent_via_sender_id"))
            ? null : r.GetInt64(r.GetOrdinal("sent_via_sender_id")),
        ImportedAt = DateTime.Parse(r.GetString(r.GetOrdinal("imported_at"))),
        SourceFile = r.IsDBNull(r.GetOrdinal("source_file"))
            ? null : r.GetString(r.GetOrdinal("source_file")),
        BatchId = r.IsDBNull(r.GetOrdinal("batch_id"))
            ? null : r.GetString(r.GetOrdinal("batch_id")),
        VerifiedAt = r.IsDBNull(r.GetOrdinal("verified_at"))
            ? null : DateTime.Parse(r.GetString(r.GetOrdinal("verified_at"))),
        PendingRetryAt = r.IsDBNull(r.GetOrdinal("pending_retry_at"))
            ? null : ParseUtc(r.GetString(r.GetOrdinal("pending_retry_at"))),
        UnconfirmedCount = r.GetInt32(r.GetOrdinal("unconfirmed_count")),
    };
}