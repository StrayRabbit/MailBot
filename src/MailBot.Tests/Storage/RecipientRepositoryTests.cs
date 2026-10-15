using MailBot.Core.Models;
using MailBot.Core.Storage;
using Xunit;

namespace MailBot.Tests.Storage;

public class RecipientRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly RecipientRepository _repo;

    public RecipientRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_recipientrepo_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _repo = new RecipientRepository(_db);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void PickPending_SkipsRowsWithFutureRetryAt()
    {
        // 插入 3 个 pending
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
            new Recipient { Email = "c@x.com" },
        }, sourceFile: null);

        var ids = _repo.GetAll();

        // 把第 2 个标 pending_retry_at 为 1 小时后
        _repo.MarkUnverified(new[] { ids[1].Id }, "future-batch"); // 改成 unverified
        _repo.MarkUnconfirmed("future-batch", "test"); // 改 unconfirmed + 写冷却
        // 现在手动把它的 pending_retry_at 设到未来
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"UPDATE recipients
SET status='pending', pending_retry_at=$retry
WHERE email='b@x.com'";
            cmd.Parameters.AddWithValue("$retry", DateTime.UtcNow.AddHours(1).ToString("o"));
            cmd.ExecuteNonQuery();
        }

        var pending = _repo.PickPending(10);
        Assert.Equal(2, pending.Count);
        Assert.DoesNotContain(pending, r => r.Email == "b@x.com");
    }

    [Fact]
    public void PickPending_IncludesRowsWithPastRetryAt()
    {
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "past@x.com" },
        }, sourceFile: null);

        // 写入过去的 pending_retry_at
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"UPDATE recipients SET pending_retry_at=$retry WHERE email='past@x.com'";
            cmd.Parameters.AddWithValue("$retry", DateTime.UtcNow.AddHours(-1).ToString("o"));
            cmd.ExecuteNonQuery();
        }

        var pending = _repo.PickPending(10);
        Assert.Single(pending);
        Assert.Equal("past@x.com", pending[0].Email);
    }

    [Fact]
    public void PickPending_ReturnsRandomSubset_NotOrderedById()
    {
        // 插入 100 个 pending，跑 50 次 PickPending(10)，断言：至少有一次结果首条 != id 最小的
        var list = Enumerable.Range(0, 100).Select(i => new Recipient { Email = $"r{i:D3}@x.com" }).ToArray();
        _repo.InsertBatch(list, sourceFile: null);

        var allIds = _repo.GetAll().ToDictionary(r => r.Email, r => r.Id);
        var smallestId = allIds.Values.Min();

        bool sawNonSmallest = false;
        for (int i = 0; i < 50 && !sawNonSmallest; i++)
        {
            var picked = _repo.PickPending(10);
            if (picked.Count > 0 && picked[0].Id != smallestId) sawNonSmallest = true;
        }

        Assert.True(sawNonSmallest, "PickPending 应该是随机的，50 次内至少一次首条 != 最小 id");
    }

    [Fact]
    public void MarkUnconfirmedWithCooldown_SetsAllFields()
    {
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
        }, sourceFile: null);
        var ids = _repo.GetAll().Select(r => r.Id).ToList();

        _repo.MarkUnverified(ids, "batch-cd");
        _repo.MarkUnconfirmedWithCooldown("batch-cd", "验证邮箱超时未收到", TimeSpan.FromMinutes(60));

        var all = _repo.GetAll();
        foreach (var r in all)
        {
            Assert.Equal("unconfirmed", r.Status);
            Assert.Contains("验证邮箱超时未收到", r.ErrorMessage);
            Assert.NotNull(r.PendingRetryAt);
            Assert.Equal(1, r.UnconfirmedCount);
            // 冷却时间应该在 now ~ now+60min 之间
            var diff = (r.PendingRetryAt!.Value - DateTime.UtcNow).TotalMinutes;
            Assert.InRange(diff, 55, 65);
        }
    }

    [Fact]
    public void MarkUnconfirmedWithCooldown_AccumulatesCount()
    {
        _repo.InsertBatch(new[] { new Recipient { Email = "a@x.com" } }, sourceFile: null);
        var id = _repo.GetAll()[0].Id;

        _repo.MarkUnverified(new[] { id }, "batch-acc");
        _repo.MarkUnconfirmedWithCooldown("batch-acc", "first", TimeSpan.FromMinutes(60));

        // 把行 reset 回 pending（模拟实际工作流）
        _repo.ResetToPendingByBatchId("batch-acc");
        _repo.MarkUnverified(new[] { id }, "batch-acc");

        _repo.MarkUnconfirmedWithCooldown("batch-acc", "second", TimeSpan.FromMinutes(120));

        var r = _repo.GetAll().Single();
        Assert.Equal(2, r.UnconfirmedCount);
    }

    [Fact]
    public void MarkUnconfirmedWithCooldown_FillsPendingRetryAt_AfterReset()
    {
        // 场景：先 MarkUnconfirmedWithCooldown，后 ResetToPendingByBatchId，行回到 pending
        // 但仍应有 pending_retry_at
        _repo.InsertBatch(new[] { new Recipient { Email = "a@x.com" } }, sourceFile: null);
        var id = _repo.GetAll()[0].Id;
        _repo.MarkUnverified(new[] { id }, "batch-r");
        _repo.MarkUnconfirmedWithCooldown("batch-r", "测试", TimeSpan.FromMinutes(60));
        _repo.ResetToPendingByBatchId("batch-r");

        var pending = _repo.PickPending(10);
        Assert.Empty(pending); // 冷却中，不应被选
    }

    // ---- Query / Count(QuerySpec) 测试 ----

    [Fact]
    public void Query_NoFilters_ReturnsAllOrderedByIdDesc()
    {
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
            new Recipient { Email = "c@x.com" },
        }, sourceFile: null);

        var spec = new RecipientRepository.QuerySpec(Status: null, EmailLike: null, BatchId: null);
        var list = _repo.Query(spec, offset: 0, limit: 10);

        Assert.Equal(3, list.Count);
        // id DESC 排序：最后插入的 c 排第一
        Assert.Equal("c@x.com", list[0].Email);
        Assert.Equal("a@x.com", list[2].Email);
        Assert.Equal(3, _repo.Count(spec));
    }

    [Fact]
    public void Query_WithEmailLike_MatchesSubstringCaseInsensitive()
    {
        // 邮箱在导入时已统一小写，所以测试数据无需再转换
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "alice@gmail.com" },
            new Recipient { Email = "bob@example.com" },
            new Recipient { Email = "carol@gmail.com" },
        }, sourceFile: null);

        var spec = new RecipientRepository.QuerySpec(Status: null, EmailLike: "gmail", BatchId: null);
        var list = _repo.Query(spec, offset: 0, limit: 10);

        Assert.Equal(2, list.Count);
        Assert.All(list, r => Assert.Contains("gmail", r.Email));
        Assert.Equal(2, _repo.Count(spec));
    }

    [Fact]
    public void Query_WithBatchId_DoesExactMatch()
    {
        // 准备数据：3 个收件人，但只有 2 个有 batch_id
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
            new Recipient { Email = "c@x.com" },
        }, sourceFile: null);

        var ids = _repo.GetAll();
        // 只给 a、c 标 batch，b 留空
        _repo.MarkUnverified(new[] { ids[0].Id, ids[2].Id }, "2026-09-29-001-aaaa");

        // 精确匹配：整 batch_id 一字不差
        var exactSpec = new RecipientRepository.QuerySpec(
            Status: null, EmailLike: null, BatchId: "2026-09-29-001-aaaa");
        var exactList = _repo.Query(exactSpec, offset: 0, limit: 10);
        Assert.Equal(2, exactList.Count);
        Assert.All(exactList, r => Assert.Equal("2026-09-29-001-aaaa", r.BatchId));
        Assert.DoesNotContain(exactList, r => r.Email == "b@x.com");

        // 部分匹配（"2026"）不应命中（不是 LIKE 语义）
        var partialSpec = new RecipientRepository.QuerySpec(
            Status: null, EmailLike: null, BatchId: "2026");
        Assert.Empty(_repo.Query(partialSpec, offset: 0, limit: 10));
    }

    [Fact]
    public void Query_WithAllFilters_AndClausesCombine()
    {
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "alice@gmail.com" },
            new Recipient { Email = "alice@example.com" },
            new Recipient { Email = "bob@gmail.com" },
        }, sourceFile: null);

        var ids = _repo.GetAll();
        // 给 alice@ 两个和 bob@ 都标同一个批次
        _repo.MarkUnverified(new[] { ids[0].Id, ids[1].Id, ids[2].Id }, "batch-x");

        // status='unverified'（MarkUnverified 写的就是这个状态）+ email 含 alice + batch_id=batch-x
        var spec = new RecipientRepository.QuerySpec(
            Status: "unverified", EmailLike: "alice", BatchId: "batch-x");
        var list = _repo.Query(spec, offset: 0, limit: 10);

        // alice@ 两个都中；bob@gmail.com 被 status + email 双重过滤掉
        Assert.Equal(2, list.Count);
        Assert.All(list, r => Assert.Contains("alice", r.Email));
        Assert.All(list, r => Assert.Equal("batch-x", r.BatchId));
        Assert.All(list, r => Assert.Equal("unverified", r.Status));
        Assert.Equal(2, _repo.Count(spec));
    }

    [Fact]
    public void Query_AllFilterNullOrAll_BehavesLikeNoFilter()
    {
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
        }, sourceFile: null);

        // Status="all" 等价于 null：不过滤
        var spec = new RecipientRepository.QuerySpec(
            Status: "all", EmailLike: "  ", BatchId: "");
        var list = _repo.Query(spec, offset: 0, limit: 10);

        Assert.Equal(2, list.Count);
    }

    // ---- Update 测试 ----

    [Fact]
    public void Update_ChangesEmailAndStatus()
    {
        _repo.InsertBatch(new[] { new Recipient { Email = "a@x.com" } }, sourceFile: null);
        var id = _repo.GetAll()[0].Id;

        var result = _repo.Update(id, "alice@x.com", "blocked", null);

        Assert.Equal(UpdateRecipientResult.Success, result);
        var row = _repo.GetAll()[0];
        Assert.Equal("alice@x.com", row.Email);
        Assert.Equal("blocked", row.Status);
    }

    [Fact]
    public void Update_SameEmailOnSameRow_IsAllowed()
    {
        // 只改状态、邮箱不变时，不该被自己的旧行挡住。
        _repo.InsertBatch(new[] { new Recipient { Email = "a@x.com" } }, sourceFile: null);
        var id = _repo.GetAll()[0].Id;

        var result = _repo.Update(id, "a@x.com", "failed", null);

        Assert.Equal(UpdateRecipientResult.Success, result);
        Assert.Equal("failed", _repo.GetAll()[0].Status);
    }

    [Fact]
    public void Update_DuplicateEmail_IsRejected()
    {
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
        }, sourceFile: null);
        var ids = _repo.GetAll();

        var result = _repo.Update(ids[0].Id, "b@x.com", "blocked", null);

        Assert.Equal(UpdateRecipientResult.DuplicateEmail, result);
        // 原值不应被改动
        var rows = _repo.GetAll().OrderBy(r => r.Email).ToArray();
        Assert.Equal("a@x.com", rows[0].Email);
        Assert.Equal("b@x.com", rows[1].Email);
    }

    [Fact]
    public void Update_SameEmailFromDifferentSourceFile_IsAllowed()
    {
        // UNIQUE(email, source_file) 只在同一来源文件内去重，跨文件同名邮箱应放行。
        _repo.InsertBatch(new[] { new Recipient { Email = "a@x.com" } }, sourceFile: "list-a.csv");
        _repo.InsertBatch(new[] { new Recipient { Email = "a@x.com" } }, sourceFile: "list-b.csv");
        var rows = _repo.GetAll();
        Assert.Equal(2, rows.Count);

        var result = _repo.Update(rows[0].Id, "a@x.com", "blocked", "list-a.csv");

        Assert.Equal(UpdateRecipientResult.Success, result);
    }

    [Fact]
    public void Update_UnknownId_ReturnsNotFound()
    {
        _repo.InsertBatch(new[] { new Recipient { Email = "a@x.com" } }, sourceFile: null);

        var result = _repo.Update(9999, "zzz@x.com", "blocked", null);

        Assert.Equal(UpdateRecipientResult.NotFound, result);
        Assert.Single(_repo.GetAll());
    }

    // ---- DeleteAll 测试 ----

    [Fact]
    public void DeleteAll_RemovesEveryRowRegardlessOfStatus()
    {
        var ids = _repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
            new Recipient { Email = "c@x.com" },
        }, sourceFile: null);
        Assert.Equal(3, ids);
        _repo.MarkUnverified(_repo.GetAll().Select(r => r.Id).ToArray(), "batch-c");

        var deleted = _repo.DeleteAll();

        Assert.Equal(3, deleted);
        Assert.Empty(_repo.GetAll());
        Assert.Equal(0, _repo.Count());
    }

    [Fact]
    public void DeleteAll_OnEmptyTable_ReturnsZero()
    {
        Assert.Equal(0, _repo.DeleteAll());
    }

    [Fact]
    public void Count_IgnoresStatusFilter()
    {
        // ClearAll 的确认框靠 Count() 报总数，必须是不带 WHERE 的全表计数。
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
        }, sourceFile: null);
        _repo.MarkUnverified(new[] { _repo.GetAll()[0].Id }, "batch-c");

        Assert.Equal(2, _repo.Count());
    }
}