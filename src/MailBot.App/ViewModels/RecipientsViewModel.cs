using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailBot.Core.Models;
using MailBot.Core.Recipients;
using MailBot.Core.Storage;

namespace MailBot.App.ViewModels;

/// <summary>
/// 收件人列表视图模型（分页版）。
/// - 服务端分页：DB 每次只返回当前页 50 条；DataGrid 只渲染 50 行，无虚拟化压力。
/// - 跨页勾选：用 <see cref="_selectedIds"/> 跟踪已勾选的 Recipient.Id，跨页保持。
/// - 切换筛选时把 <see cref="PageIndex"/> 归零。
/// </summary>
public sealed partial class RecipientsViewModel : ObservableObject
{
    private readonly RecipientRepository _repo;

    /// <summary>跨页累计勾选的收件人 ID 集合，避免切页后丢失选择状态。</summary>
    private readonly HashSet<long> _selectedIds = new();

    /// <summary>当前页（50 条）。整体替换，1 次 PropertyChanged。</summary>
    [ObservableProperty] private ObservableCollection<RecipientRow> _recipients = new();

    [ObservableProperty] private string _filterStatus = "all";

    /// <summary>
    /// 统一查询关键字：默认按邮箱子串查（除非看起来像批次号，见 <see cref="BuildSpec"/>）。
    /// 空串=不过滤。
    /// </summary>
    [ObservableProperty] private string _queryText = string.Empty;

    [ObservableProperty] private int _selectedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _pageIndex = 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _totalCount;

    public int PageSize { get; } = 50;

    /// <summary>
    /// 收件人表的实际总行数，**不受筛选/分页影响**。
    /// 与 <see cref="TotalCount"/>（筛选后的条数）区分开，用于"清空所有"这类
    /// 需要向用户报出真实影响面的操作。
    /// </summary>
    public int TotalInTable => _repo.Count();

    public int PageCount => PageSize > 0 ? (TotalCount + PageSize - 1) / PageSize : 0;

    /// <summary>1-based 页码（用于显示）。</summary>
    public int PageIndexDisplay => PageIndex + 1;

    // 计算属性 PageCount / PageIndexDisplay 自身没有 [ObservableProperty]，
    // 不主动通知 PropertyChanged；显式在依赖的 ObservableProperty 变化时触发。
    partial void OnPageIndexChanged(int value) => OnPropertyChanged(nameof(PageIndexDisplay));
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(PageCount));

    /// <summary>当前页所有行都已勾选时为 true。用于"全选/取消全选"按钮的标签切换。</summary>
    public bool AllSelected => Recipients.Count > 0 && Recipients.All(r => r.IsSelected);

    /// <summary>按钮标签：当前未全选时显示"全选"，全选时显示"取消全选"。</summary>
    public string ToggleAllLabel => AllSelected ? "取消全选" : "全选";

    public RecipientsViewModel(RecipientRepository repo)
    {
        _repo = repo;
        Refresh();
    }

    public void Refresh()
    {
        // 解绑旧行（避免旧 RecipientRow 因事件订阅被 GC 根持有）
        foreach (var old in Recipients)
        {
            old.PropertyChanged -= OnRowPropertyChanged;
        }

        // 构造统一查询条件。看 <see cref="BuildSpec"/> 的派发规则。
        var spec = BuildSpec();

        // 拉总数（cheap，单独 COUNT(*) 查询）
        TotalCount = _repo.Count(spec);

        // 筛选缩小导致 PageIndex 越界时，clamp 到最后一页
        if (PageCount > 0 && PageIndex >= PageCount)
        {
            PageIndex = PageCount - 1;
        }
        else if (PageCount == 0)
        {
            PageIndex = 0;
        }

        var offset = PageIndex * PageSize;
        var list = _repo.Query(spec, offset, PageSize);

        var rows = new ObservableCollection<RecipientRow>();
        // 全局序号：当前页起点 + 行内偏移，从 1 开始
        var startIndex = PageIndex * PageSize;
        foreach (var r in list)
        {
            var row = new RecipientRow(r) { RowNumber = ++startIndex };
            row.PropertyChanged += OnRowPropertyChanged;
            // 还原跨页勾选状态：构造后置 IsSelected，会触发 OnRowPropertyChanged 把 ID 加进 _selectedIds，
            // 但因为 ID 已在集合中（HashSet.Add 幂等），重复添加是 no-op。
            row.IsSelected = _selectedIds.Contains(r.Id);
            rows.Add(row);
        }
        Recipients = rows;
        UpdateSelectedCount();
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(ToggleAllLabel));
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecipientRow.IsSelected) || sender is not RecipientRow row)
        {
            return;
        }
        if (row.IsSelected) _selectedIds.Add(row.Model.Id);
        else _selectedIds.Remove(row.Model.Id);
        UpdateSelectedCount();
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(ToggleAllLabel));
    }

    private void UpdateSelectedCount() => SelectedCount = _selectedIds.Count;

    /// <summary>导入文件并返回预览统计。</summary>
    public RecipientImporter.ImportResult Import(string filePath)
    {
        var result = RecipientImporter.Import(filePath);
        if (result.Recipients.Count > 0)
        {
            _repo.InsertBatch(result.Recipients, Path.GetFileName(filePath));
        }
        return result;
    }

    /// <summary>
    /// 修改单条收件人的邮箱与状态。成功后重建列表——
    /// RecipientRow 的展示属性是只读的透传，只有 Refresh 才会让 DataGrid 拿到新值。
    /// </summary>
    /// <param name="sourceFile">该行原本的来源文件，必须与仓储里的一致才能正确命中唯一约束。</param>
    public UpdateRecipientResult UpdateRecipient(long id, string email, string status, string? sourceFile)
    {
        var result = _repo.Update(id, email, status, sourceFile);
        if (result == UpdateRecipientResult.Success)
        {
            Refresh();
        }
        return result;
    }

    /// <summary>按当前 QuerySpec 全量导出（不受分页影响）。派发逻辑同 <see cref="Refresh"/>。</summary>
    public void Export(string outputPath)
    {
        var spec = BuildSpec();
        var total = _repo.Count(spec);
        var list = _repo.Query(spec, offset: 0, limit: total);
        RecipientExporter.Export(list, outputPath);
    }

    /// <summary>
    /// 把 VM 状态（FilterStatus + QueryText）翻译成仓储 QuerySpec。
    /// 派发规则：当 QueryText 看起来像批次号（包含 '-' 且至少一段是数字，如 "2026-09-29-001-aaaa"）
    /// 时按批次号精确匹配；否则（含 '@' 的邮箱、纯文本等）按邮箱子串查。
    /// 大多数人日常使用都是"按邮箱搜"（输入 alice / gmail / @example.com 等），所以默认走邮箱；
    /// 只有视觉上明显是批次号时（如复制粘贴的 2026-09-29-001-XXXX）才走批次号。
    /// </summary>
    private RecipientRepository.QuerySpec BuildSpec()
    {
        var hasDash = QueryText.Contains('-');
        var hasDigit = QueryText.Any(char.IsDigit);
        var treatAsBatchId = hasDash && hasDigit
            && !QueryText.Contains('@'); // 兜底：含 @ 一定不是批次号
        return new RecipientRepository.QuerySpec(
            Status: string.IsNullOrEmpty(FilterStatus) || FilterStatus == "all" ? null : FilterStatus,
            EmailLike: string.IsNullOrWhiteSpace(QueryText) ? null
                : (treatAsBatchId ? null : QueryText),
            BatchId: string.IsNullOrWhiteSpace(QueryText) ? null
                : (treatAsBatchId ? QueryText : null));
    }

    /// <summary>勾选单行（按 ID），不依赖 TwoWay 绑定。供 RecipientsPage 的 RowCheck_Click 调用。</summary>
    public void SelectRow(long id)
    {
        if (_selectedIds.Add(id))
        {
            UpdateSelectedCount();
            OnPropertyChanged(nameof(AllSelected));
            OnPropertyChanged(nameof(ToggleAllLabel));
        }
        // 同步刷新当前页该行的 IsSelected（如果可见）
        var row = Recipients.FirstOrDefault(r => r.Model.Id == id);
        if (row is not null && !row.IsSelected)
        {
            row.IsSelected = true; // 会再次触发 OnRowPropertyChanged，但 HashSet.Add 幂等
        }
    }

    /// <summary>取消勾选单行（按 ID），不依赖 TwoWay 绑定。供 RecipientsPage 的 RowCheck_Click 调用。</summary>
    public void DeselectRow(long id)
    {
        if (_selectedIds.Remove(id))
        {
            UpdateSelectedCount();
            OnPropertyChanged(nameof(AllSelected));
            OnPropertyChanged(nameof(ToggleAllLabel));
        }
        var row = Recipients.FirstOrDefault(r => r.Model.Id == id);
        if (row is not null && row.IsSelected)
        {
            row.IsSelected = false;
        }
    }

    /// <summary>把当前页所有行的 IsSelected 设为 true。</summary>
    public void SelectAll()
    {
        foreach (var r in Recipients) r.IsSelected = true;
    }

    /// <summary>把当前页所有行的 IsSelected 设为 false。</summary>
    public void DeselectAll()
    {
        foreach (var r in Recipients) r.IsSelected = false;
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        if (AllSelected) DeselectAll();
        else SelectAll();
    }

    [RelayCommand(CanExecute = nameof(CanPrevPage))]
    private void PrevPage()
    {
        if (PageIndex > 0)
        {
            PageIndex--;
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private void NextPage()
    {
        if (PageIndex < PageCount - 1)
        {
            PageIndex++;
            Refresh();
        }
    }

    private bool CanPrevPage() => PageIndex > 0;
    private bool CanNextPage() => PageIndex < PageCount - 1;

    /// <summary>按当前 QueryText 重新分页查询。供输入框回车 / 搜索按钮触发。</summary>
    [RelayCommand]
    private void ApplyQuery()
    {
        PageIndex = 0;
        Refresh();
    }

    /// <summary>清空查询条件并刷新。供"清空"按钮触发。</summary>
    [RelayCommand]
    private void ClearQuery()
    {
        QueryText = string.Empty;
        PageIndex = 0;
        Refresh();
    }

    /// <summary>删除所有已勾选行（跨页），返回实际删除行数。</summary>
    public int DeleteSelected()
    {
        if (_selectedIds.Count == 0) return 0;
        var ids = _selectedIds.ToList();
        var n = _repo.Delete(ids);
        _selectedIds.Clear();
        Refresh();
        return n;
    }

    /// <summary>
    /// 清空整张收件人表（不受筛选/分页/勾选影响），返回实际删除行数。
    /// 调用方负责在执行前取得用户二次确认——此操作不可恢复。
    /// </summary>
    public int ClearAll()
    {
        var n = _repo.DeleteAll();
        _selectedIds.Clear();
        Refresh();
        return n;
    }

    /// <summary>把所有已勾选行重置为 pending（跨页），返回实际重置行数。</summary>
    public int ResetSelected()
    {
        if (_selectedIds.Count == 0) return 0;
        var ids = _selectedIds.ToList();
        var n = _repo.ResetToPending(ids);
        _selectedIds.Clear();
        Refresh();
        return n;
    }
}