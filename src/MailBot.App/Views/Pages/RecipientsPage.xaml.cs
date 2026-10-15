using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MailBot.App.Controls;
using MailBot.App.ViewModels;
using MailBot.Core.Storage;
using Microsoft.Win32;

namespace MailBot.App.Views.Pages;

/// <summary>收件人列表页：导入/导出、状态筛选、复选框勾选、批量重置/删除。DataContext 继承自 MainViewModel。</summary>
public partial class RecipientsPage : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public RecipientsPage()
    {
        InitializeComponent();
    }

    private void ImportRecipients_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Excel/CSV|*.xlsx;*.csv|Excel|*.xlsx|CSV|*.csv",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (Vm is null) return;
            var result = Vm.Recipients.Import(dlg.FileName);
            Vm.Recipients.Refresh();
            Vm.Monitor.RefreshStats();
            MessageBoxEx.Show($"导入完成：{result.ImportedRows} 行成功，{result.Errors.Count} 行失败",
                "导入结果", MessageBoxButton.OK);
            foreach (var err in result.Errors.Take(10))
            {
                Vm.Monitor.AppendLog($"[导入] {err}");
            }
        }
        catch (Exception ex)
        {
            MessageBoxEx.Show($"导入失败：{ex.Message}");
        }
    }

    private void ExportRecipients_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Excel|*.xlsx|CSV|*.csv",
            FileName = $"recipients_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Vm?.Recipients.Export(dlg.FileName);
            MessageBoxEx.Show("导出完成");
        }
        catch (Exception ex)
        {
            MessageBoxEx.Show($"导出失败：{ex.Message}");
        }
    }

    private void StatusFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null) return;
        if (StatusFilter.SelectedItem is ComboBoxItem item)
        {
            // 优先用 Tag（数据库原值，未做改动）；fallback 到 Content 兼容旧条目。
            Vm.Recipients.FilterStatus = (item.Tag as string)
                ?? item.Content?.ToString()
                ?? "all";
            // 筛选条件变了 → 跳回第一页
            Vm.Recipients.PageIndex = 0;
            Vm.Recipients.Refresh();
        }
    }

    /// <summary>
    /// CheckBox 点击事件的安全网：当 DataGrid 进入编辑模式时，TwoWay 绑定偶尔不会把 IsChecked 写回 IsSelected，
    /// 导致 _selectedIds 没被更新。这里显式调 VM 同步选中状态，与绑定重复执行也无害（HashSet 操作幂等）。
    /// </summary>
    private void RowCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is CheckBox cb && cb.DataContext is RecipientRow row)
        {
            if (cb.IsChecked == true)
            {
                Vm.Recipients.SelectRow(row.Model.Id);
            }
            else
            {
                Vm.Recipients.DeselectRow(row.Model.Id);
            }
        }
    }

    private void ResetSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var n = Vm.Recipients.ResetSelected();
        Vm.Monitor.RefreshStats();
        if (n == 0)
        {
            MessageBoxEx.Show("请先勾选要重置的行");
        }
        else
        {
            MessageBoxEx.Show($"已将 {n} 条记录重置为待发送");
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var count = Vm.Recipients.SelectedCount;
        if (count == 0)
        {
            MessageBoxEx.Show("请先勾选要删除的行");
            return;
        }
        if (MessageBoxEx.ShowYesNo($"确定删除选中的 {count} 条收件人？", "删除确认")
            != MessageBoxResult.Yes) return;
        Vm.Recipients.DeleteSelected();
        Vm.Monitor.RefreshStats();
    }

    /// <summary>双击行等同点"编辑"。双击已高亮的行不改变 SelectedItem，直接进入编辑；
    /// 双击空白区域没有 SelectedItem，直接返回（提示会打扰只想聚焦列表的用户）。</summary>
    private void RecipientsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecipientsGrid.SelectedItem is not RecipientRow) return;
        EditRecipient_Click(sender, e);
        e.Handled = true;
    }

    private void EditRecipient_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (RecipientsGrid.SelectedItem is not RecipientRow row)
        {
            MessageBoxEx.Show("请先在列表中单击选中要编辑的行");
            return;
        }
        if (RecipientsGrid.SelectedItems.Count > 1)
        {
            MessageBoxEx.Show("一次只能编辑一条记录，请只选中一行");
            return;
        }

        var dlg = new RecipientEditDialog(row.Email, row.Status)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;

        var result = Vm.Recipients.UpdateRecipient(
            row.Id, dlg.Email, dlg.Status, row.Model.SourceFile);
        switch (result)
        {
            case UpdateRecipientResult.Success:
                Vm.Monitor.RefreshStats();
                MessageBoxEx.Show("已保存");
                break;
            case UpdateRecipientResult.DuplicateEmail:
                MessageBoxEx.Show($"邮箱 {dlg.Email} 已存在（同一来源文件下不允许重复）", "保存失败");
                break;
            case UpdateRecipientResult.NotFound:
                MessageBoxEx.Show("该记录已不存在，请刷新列表", "保存失败");
                break;
        }
    }

    /// <summary>
    /// 清空整张收件人表。破坏性且不可恢复，所以确认框里报出实际条数，
    /// 让用户在点"是"之前清楚自己删掉的是整个库而不只是当前这一页。
    /// </summary>
    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        // 必须用 TotalInTable（整表总数）而不是 TotalCount（筛选后条数），
        // 否则筛选状态下确认框会少报甚至报错条数。
        var total = Vm.Recipients.TotalInTable;
        if (total == 0)
        {
            MessageBoxEx.Show("当前没有收件人记录，无需清空");
            return;
        }

        var confirm = MessageBoxEx.ShowYesNo(
            $"确定要清空全部 {total} 条收件人吗？\n\n" +
            "这会删除所有状态的收件人记录，不只是当前筛选或勾选的行。\n" +
            "删除后无法恢复，如需保留请先导出。",
            "清空所有收件人 - 二次确认");
        if (confirm != MessageBoxResult.Yes) return;

        var deleted = Vm.Recipients.ClearAll();
        Vm.Monitor.RefreshStats();
        MessageBoxEx.Show($"已删除 {deleted} 条收件人记录");
    }

    /// <summary>查询框回车 / "搜索"按钮均直接绑到 Recipients.ApplyQueryCommand；TextBox 用 Text binding，
    /// 双向同步 VM.QueryText。清空按钮绑 ClearQueryCommand，VM 置空后 binding 自动反向更新 UI。</summary>
}