using System.Windows;
using System.Windows.Controls;
using MailBot.App.Controls;
using MailBot.Core.Recipients;

namespace MailBot.App.Views;

/// <summary>
/// 收件人编辑对话框：修改邮箱与状态。邮箱格式校验复用
/// <see cref="RecipientImporter.LooksLikeEmail"/>，与导入走同一套判定。
/// </summary>
public partial class RecipientEditDialog : Window
{
    /// <summary>保存成功后可直接写回列表的实体（调用方需把它交给仓储落库）。</summary>
    public string Email { get; private set; } = string.Empty;

    public string Status { get; private set; } = RecipientStatus.Pending;

    public RecipientEditDialog(string currentEmail, string currentStatus)
    {
        InitializeComponent();

        foreach (var status in RecipientStatus.All)
        {
            // ComboBoxItem 直接把 Content 当显示文本，用 Tag 携带真实的 DB 取值。
            StatusBox.Items.Add(new ComboBoxItem { Content = StatusText(status), Tag = status });
        }
        StatusBox.SelectedValuePath = nameof(ComboBoxItem.Tag);
        StatusBox.SelectedValue = RecipientStatus.All.Contains(currentStatus)
            ? currentStatus
            : RecipientStatus.Pending;

        EmailBox.Text = currentEmail;
    }

    private static string StatusText(string status) => status switch
    {
        RecipientStatus.Pending => "待发送",
        RecipientStatus.Sent => "已发送",
        RecipientStatus.Verified => "已送达",
        RecipientStatus.Unverified => "验证中",
        RecipientStatus.Failed => "失败",
        RecipientStatus.Blocked => "已屏蔽",
        RecipientStatus.Unconfirmed => "未确认",
        _ => status,
    };

    private bool ValidateAndFill(out string? error)
    {
        var email = EmailBox.Text.Trim().ToLowerInvariant();
        if (email.Length == 0)
        {
            error = "邮箱不能为空";
            return false;
        }
        if (!RecipientImporter.LooksLikeEmail(email))
        {
            error = $"邮箱格式不正确：{email}";
            return false;
        }

        Email = email;
        Status = StatusBox.SelectedValue as string ?? RecipientStatus.Pending;
        error = null;
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateAndFill(out var error))
        {
            MessageBoxEx.Show(this, error ?? "输入有误", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
