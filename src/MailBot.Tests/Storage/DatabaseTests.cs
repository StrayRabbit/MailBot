using MailBot.Core.Models;
using MailBot.Core.Storage;
using Xunit;

namespace MailBot.Tests.Storage;

public class DatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;

    public DatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_test_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void Schema_Idempotent()
    {
        // EnsureSchema 多次调用不应报错
        _db.EnsureSchema();
        _db.EnsureSchema();
        // 验证 senders 表存在
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM senders";
        Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void SenderRepository_InsertAndGet()
    {
        var repo = new SenderRepository(_db);
        var sender = new Sender
        {
            Email = "test@example.com",
            SmtpHost = "smtp.example.com",
            SmtpPort = 465,
            UseSsl = true,
            Username = "test",
            PasswordEncrypted = new byte[] { 1, 2, 3 },
            DailyQuota = 100,
            QuotaResetDate = "2026-09-25",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var id = repo.Insert(sender);
        var loaded = repo.GetById(id);
        Assert.NotNull(loaded);
        Assert.Equal("test@example.com", loaded!.Email);
        Assert.Equal(465, loaded.SmtpPort);
        Assert.True(loaded.UseSsl);
    }

    [Fact]
    public void RecipientRepository_ImportAndPick()
    {
        var senderRepo = new SenderRepository(_db);
        var repo = new RecipientRepository(_db);
        var senderId = senderRepo.Insert(new Sender
        {
            Email = "sender@x.com",
            SmtpHost = "h",
            SmtpPort = 465,
            Username = "u",
            PasswordEncrypted = Array.Empty<byte>(),
            QuotaResetDate = "2026-09-25",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var recipients = new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
            new Recipient { Email = "c@x.com" },
        };
        var inserted = repo.InsertBatch(recipients, "test.csv");
        Assert.Equal(3, inserted);

        var pending = repo.PickPending(10);
        Assert.Equal(3, pending.Count);

        repo.MarkSent(pending[0].Id, senderId);
        repo.MarkFailed(pending[1].Id, "invalid");

        Assert.Equal(1, repo.CountByStatus("sent"));
        Assert.Equal(1, repo.CountByStatus("failed"));
        Assert.Equal(1, repo.CountByStatus("pending"));
    }

    [Fact]
    public void RecipientRepository_Delete_RemovesRows()
    {
        var repo = new RecipientRepository(_db);
        var ids = repo.InsertBatch(new[]
        {
            new Recipient { Email = "del1@x.com" },
            new Recipient { Email = "del2@x.com" },
            new Recipient { Email = "keep@x.com" },
        }, "del.csv");
        Assert.Equal(3, ids);

        var all = repo.GetAll();
        var toDelete = all.Where(r => r.Email.StartsWith("del")).Select(r => r.Id).ToList();
        var deleted = repo.Delete(toDelete);
        Assert.Equal(2, deleted);
        Assert.Equal(1, repo.Count());
        Assert.Equal("keep@x.com", repo.GetAll()[0].Email);
    }

    [Fact]
    public void RecipientRepository_Delete_Empty_NoOp()
    {
        var repo = new RecipientRepository(_db);
        Assert.Equal(0, repo.Delete(Array.Empty<long>()));
        Assert.Equal(0, repo.Count());
    }

    [Fact]
    public void RecipientRepository_Delete_MarkedSent_SucceedsWithoutForeignKeyError()
    {
        // send_logs 表已移除。MarkSent 后 recipient 与 sender 关联，
        // 现在删除应该直接成功（无外键引用需要级联清理）。
        var senderRepo = new SenderRepository(_db);
        var repo = new RecipientRepository(_db);
        var senderId = senderRepo.Insert(new Sender
        {
            Email = "s@x.com", SmtpHost = "h", SmtpPort = 465, Username = "u",
            PasswordEncrypted = Array.Empty<byte>(), QuotaResetDate = "2026-09-25",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var ids = repo.InsertBatch(new[]
        {
            new Recipient { Email = "del-a@x.com" },
            new Recipient { Email = "del-b@x.com" },
        }, "del-with-logs.csv");
        Assert.Equal(2, ids);

        var all = repo.GetAll();
        foreach (var r in all) repo.MarkSent(r.Id, senderId);

        // 直接删除：应该返回 2 行
        var n = repo.Delete(all.Select(r => r.Id));
        Assert.Equal(2, n);
        Assert.Equal(0, repo.Count());
    }

    [Fact]
    public void RecipientRepository_ResetToPending_FlipsSentAndFailed()
    {
        var repo = new RecipientRepository(_db);
        repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
            new Recipient { Email = "c@x.com" },
            new Recipient { Email = "d@x.com" },
        }, "reset.csv");

        var all = repo.GetAll();
        var senderId = new SenderRepository(_db).Insert(new Sender
        {
            Email = "s@x.com", SmtpHost = "h", SmtpPort = 465, Username = "u",
            PasswordEncrypted = Array.Empty<byte>(), QuotaResetDate = "2026-09-25",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        repo.MarkSent(all[0].Id, senderId);     // a: sent
        repo.MarkFailed(all[1].Id, "boom");     // b: failed
        repo.IncrementAttempt(all[0].Id);       // a: attempt_count=1
        repo.IncrementAttempt(all[2].Id);       // c: attempt_count=1（pending 也累加）
                                               // d 保持 attempt_count=0

        var resetIds = new[] { all[0].Id, all[1].Id, all[2].Id };
        var n = repo.ResetToPending(resetIds);
        // 不再判断源状态,pending 也会被 UPDATE
        Assert.Equal(3, n);

        var after = repo.GetAll();
        var a = after.Single(r => r.Email == "a@x.com");
        Assert.Equal("pending", a.Status);
        Assert.Null(a.SentAt);
        Assert.Null(a.ErrorMessage);
        Assert.Equal(0, a.AttemptCount);

        var b = after.Single(r => r.Email == "b@x.com");
        Assert.Equal("pending", b.Status);
        Assert.Null(b.ErrorMessage);
        Assert.Equal(0, b.AttemptCount);

        var c = after.Single(r => r.Email == "c@x.com");
        Assert.Equal("pending", c.Status);
        Assert.Equal(0, c.AttemptCount);

        var d = after.Single(r => r.Email == "d@x.com");
        Assert.Equal(0, d.AttemptCount); // 未参与重置,保持 0
    }

    [Fact]
    public void RecipientRepository_ResetToPending_Empty_NoOp()
    {
        var repo = new RecipientRepository(_db);
        Assert.Equal(0, repo.ResetToPending(Array.Empty<long>()));
    }

    [Fact]
    public void RecipientRepository_ResetToPendingByBatchId_ResetsAllInBatch()
    {
        var repo = new RecipientRepository(_db);
        repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
            new Recipient { Email = "c@x.com" },
            new Recipient { Email = "d@x.com" },
        }, "by-batch.csv");

        var all = repo.GetAll();
        // InsertBatch 不落 batch_id,这里手工写库(本次测试只关注 ResetToPendingByBatchId 的行为)
        AssignBatchId(all[0].Id, "batch-A");
        AssignBatchId(all[1].Id, "batch-A");
        AssignBatchId(all[2].Id, "batch-B");
        // all[3] 不分配 batch

        var senderId = new SenderRepository(_db).Insert(new Sender
        {
            Email = "s@x.com", SmtpHost = "h", SmtpPort = 465, Username = "u",
            PasswordEncrypted = Array.Empty<byte>(), QuotaResetDate = "2026-09-25",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        repo.MarkSent(all[0].Id, senderId);
        repo.MarkFailed(all[1].Id, "boom");
        repo.IncrementAttempt(all[0].Id);
        repo.IncrementAttempt(all[2].Id);

        var n = repo.ResetToPendingByBatchId("batch-A");
        Assert.Equal(2, n);

        var after = repo.GetAll();
        var a = after.Single(r => r.Email == "a@x.com");
        Assert.Equal("pending", a.Status);
        Assert.Null(a.SentAt);
        Assert.Equal(0, a.AttemptCount);

        var b = after.Single(r => r.Email == "b@x.com");
        Assert.Equal("pending", b.Status);
        Assert.Null(b.ErrorMessage);
        Assert.Equal(0, b.AttemptCount);

        // 其他 batch 的不受影响（c 之前 IncrementAttempt 过,保留 1）
        var c = after.Single(r => r.Email == "c@x.com");
        Assert.Equal(1, c.AttemptCount);
        var d = after.Single(r => r.Email == "d@x.com");
        Assert.Equal("pending", d.Status);
    }

    private void AssignBatchId(long recipientId, string batchId)
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE recipients SET batch_id=$bid WHERE id=$id";
        cmd.Parameters.AddWithValue("$bid", batchId);
        cmd.Parameters.AddWithValue("$id", recipientId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void RecipientRepository_ResetToPendingByBatchId_EmptyOrUnknown_NoOp()
    {
        var repo = new RecipientRepository(_db);
        Assert.Equal(0, repo.ResetToPendingByBatchId(""));
        Assert.Equal(0, repo.ResetToPendingByBatchId("does-not-exist"));
    }

    [Fact]
    public void ConfigRepository_KV_Roundtrip()
    {
        var repo = new ConfigRepository(_db);
        repo.Set("window_start", "08:00");
        repo.Set("batch_size", "30");
        repo.Set("custom_key", "hello");

        var cfg = repo.Load();
        Assert.Equal("08:00", cfg.WindowStart);
        Assert.Equal(30, cfg.BatchSize);
        Assert.Equal("hello", cfg.Extra["custom_key"]);

        cfg.WindowStart = "10:00";
        repo.Save(cfg);
        Assert.Equal("10:00", repo.Get("window_start"));
    }

    [Fact]
    public void SenderRepository_UniqueEmail_Throws()
    {
        var repo = new SenderRepository(_db);
        var s = new Sender
        {
            Email = "dup@x.com",
            SmtpHost = "h", SmtpPort = 465, Username = "u",
            PasswordEncrypted = Array.Empty<byte>(),
            QuotaResetDate = "2026-09-25",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        repo.Insert(s);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => repo.Insert(s));
    }

    [Fact]
    public void SenderRepository_UpdateStatus_NullCooldown_NoThrow()
    {
        // 之前 cooldownUntil=null 时直接 AddWithValue 绑成 C# null，
        // SqliteParameter.Bind 看到 Value=null 抛 "Value must be set."——必须用 DBNull.Value。
        var repo = new SenderRepository(_db);
        var id = repo.Insert(new Sender
        {
            Email = "nocooldown@x.com",
            SmtpHost = "h", SmtpPort = 465, Username = "u",
            PasswordEncrypted = Array.Empty<byte>(),
            QuotaResetDate = "2026-09-25",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        repo.UpdateStatus(id, "normal", 0, null);

        var s = repo.GetById(id);
        Assert.NotNull(s);
        Assert.Equal("normal", s!.Status);
        Assert.Null(s.CooldownUntil);
    }

    [Fact]
    public void SenderRepository_UpdateStatus_WithCooldown_StoresValue()
    {
        var repo = new SenderRepository(_db);
        var id = repo.Insert(new Sender
        {
            Email = "withcooldown@x.com",
            SmtpHost = "h", SmtpPort = 465, Username = "u",
            PasswordEncrypted = Array.Empty<byte>(),
            QuotaResetDate = "2026-09-25",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var until = new DateTime(2026, 12, 1, 10, 30, 0, DateTimeKind.Utc);

        repo.UpdateStatus(id, "cooling", 1, until);

        var s = repo.GetById(id);
        Assert.NotNull(s);
        Assert.Equal("cooling", s!.Status);
        Assert.Equal(1, s.ConsecutiveFailures);
        Assert.NotNull(s.CooldownUntil);
        Assert.Contains("2026-12-01", s.CooldownUntil!);
    }

    [Fact]
    public void RecipientRepository_MarkUnverified_AndVerified_Roundtrip()
    {
        var repo = new RecipientRepository(_db);
        repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
        }, "v.csv");
        var ids = repo.GetAll().Select(r => r.Id).ToList();

        repo.MarkUnverified(ids, "batch-1");
        var unverified = repo.GetByStatus("unverified");
        Assert.Equal(2, unverified.Count);
        Assert.All(unverified, r => Assert.Equal("batch-1", r.BatchId));

        repo.MarkVerified("batch-1", DateTime.UtcNow);
        var verified = repo.GetByStatus("verified");
        Assert.Equal(2, verified.Count);
        Assert.All(verified, r => Assert.NotNull(r.VerifiedAt));
    }

    [Fact]
    public void RecipientRepository_MarkUnconfirmed_StoresReason()
    {
        var repo = new RecipientRepository(_db);
        repo.InsertBatch(new[] { new Recipient { Email = "x@y.com" } }, "u.csv");
        var ids = repo.GetAll().Select(r => r.Id).ToList();
        repo.MarkUnverified(ids, "batch-2");
        repo.MarkUnconfirmed("batch-2", "验证邮箱超时");
        var r = repo.GetByStatus("unconfirmed").Single();
        Assert.Contains("验证邮箱超时", r.ErrorMessage);
    }
}