using MailBot.App.ViewModels;
using MailBot.Core.Models;
using MailBot.Core.Storage;
using Xunit;

namespace MailBot.Tests.App;

/// <summary>RecipientsViewModel 的查询派发测试。
/// 只验证 QueryText → QuerySpec 的派发（仓储层在 RecipientRepositoryTests 里已覆盖）。</summary>
public class RecipientsViewModelQueryDispatchTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly RecipientRepository _repo;
    private readonly RecipientsViewModel _vm;

    public RecipientsViewModelQueryDispatchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_recvmd_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _repo = new RecipientRepository(_db);

        // 准备混合数据：一个 @ 一个批次号
        _repo.InsertBatch(new[]
        {
            new Recipient { Email = "alice@gmail.com" },
            new Recipient { Email = "bob@example.com" },
            new Recipient { Email = "carol@example.com" },
        }, sourceFile: null);
        var ids = _repo.GetAll();
        _repo.MarkUnverified(new[] { ids[1].Id }, "2026-09-29-001-aaaa"); // bob 带批次

        _vm = new RecipientsViewModel(_repo);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void QueryText_WithEmailSubstring_FiltersByEmail()
    {
        // "gmail" 不含 '-', 派发到邮箱子串查
        _vm.QueryText = "gmail";
        _vm.ApplyQueryCommand.Execute(null);

        Assert.Equal(1, _vm.TotalCount);
        Assert.Single(_vm.Recipients);
        Assert.Equal("alice@gmail.com", _vm.Recipients[0].Email);
    }

    [Fact]
    public void QueryText_WithAtSymbol_FiltersByEmailSubstring()
    {
        // "example.com" 含 '.' 但不含 '-', 派发到邮箱子串查
        _vm.QueryText = "example.com";
        _vm.ApplyQueryCommand.Execute(null);

        Assert.Equal(2, _vm.TotalCount);
        Assert.Contains(_vm.Recipients, r => r.Email == "bob@example.com");
        Assert.Contains(_vm.Recipients, r => r.Email == "carol@example.com");
    }

    [Fact]
    public void QueryText_LooksLikeBatchId_FiltersByBatchIdExact()
    {
        // 含 '-' 且含数字 → 派发到批次号精确查
        _vm.QueryText = "2026-09-29-001-aaaa";
        _vm.ApplyQueryCommand.Execute(null);

        // 仅 bob 被标了该批次号
        Assert.Equal(1, _vm.TotalCount);
        Assert.Single(_vm.Recipients);
        Assert.Equal("bob@example.com", _vm.Recipients[0].Email);
        Assert.Equal("2026-09-29-001-aaaa", _vm.Recipients[0].BatchId);
    }

    [Fact]
    public void QueryText_Empty_DoesNotFilter()
    {
        _vm.QueryText = "";
        _vm.ApplyQueryCommand.Execute(null);

        Assert.Equal(3, _vm.TotalCount);
    }

    [Fact]
    public void ClearQuery_ResetsQueryTextAndRefreshes()
    {
        _vm.QueryText = "gmail";
        _vm.ApplyQueryCommand.Execute(null);
        Assert.Equal(1, _vm.TotalCount);

        _vm.ClearQueryCommand.Execute(null);

        Assert.Equal(string.Empty, _vm.QueryText);
        Assert.Equal(3, _vm.TotalCount);
        Assert.Equal(0, _vm.PageIndex);
    }

    [Fact]
    public void QueryText_CombineWithStatusFilter_AndClause()
    {
        // 给 alice 标另一个批次，让她同时匹配 "alice" 邮箱 + "batch-aaa" 批次
        var allIds = _repo.GetAll();
        _repo.MarkUnverified(new[] { allIds[0].Id }, "batch-aaa");
        // 现在 alice 处于 unverified 状态 + batch=batch-aaa

        // 先按邮箱查 alice（"alice" 不含 '-', 派发到邮箱子串）
        _vm.QueryText = "alice";
        _vm.ApplyQueryCommand.Execute(null);
        Assert.Equal(1, _vm.TotalCount);

        // 切到 unverified 状态 + 邮箱 alice 仍然命中（AND 关系）
        _vm.FilterStatus = "unverified";
        _vm.PageIndex = 0;
        _vm.Refresh();
        Assert.Equal(1, _vm.TotalCount);
        Assert.Equal("alice@gmail.com", _vm.Recipients[0].Email);
    }

    [Fact]
    public void TotalInTable_IgnoresFilter_ReportsWholeTable()
    {
        // 确认框报数用的是 TotalInTable，不能被状态筛选带偏。
        _vm.FilterStatus = "unverified";
        _vm.Refresh();
        Assert.Equal(1, _vm.TotalCount);   // 筛选后只有 1 条

        Assert.Equal(3, _vm.TotalInTable); // 整表仍是 3 条
    }

    [Fact]
    public void ClearAll_EmptiesTableAndSelection()
    {
        // 勾选一行，验证清空后跨页勾选集也被清掉
        _vm.SelectRow(_repo.GetAll()[0].Id);
        Assert.Equal(1, _vm.SelectedCount);

        var deleted = _vm.ClearAll();

        Assert.Equal(3, deleted);
        Assert.Empty(_vm.Recipients);
        Assert.Equal(0, _vm.TotalCount);
        Assert.Equal(0, _vm.TotalInTable);
        Assert.Equal(0, _vm.SelectedCount);
    }
}