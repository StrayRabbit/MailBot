using System.Windows;
using System.Windows.Controls;
using MailBot.App.Controls;
using MailBot.App.ViewModels;
using MailBot.App.Views;
using MailBot.Core.Models;
using MailBot.Core.Sending;

namespace MailBot.App.Views.Pages;

/// <summary>
/// 账号页：发件账号 + 验证邮箱。
/// 构造时注入 SmtpDispatcher（用于 SenderEditDialog 的 SMTP 测试连接）和 SenderRepository
/// （账号 CRUD 当前直接走仓储，由本页 code-behind 触发；ViewModel 只负责列表展示与刷新）。
/// DataContext 继承自 MainViewModel。
/// </summary>
public partial class AccountsPage : UserControl
{
    private readonly SmtpDispatcher _dispatcher;

    private MainViewModel? Vm => DataContext as MainViewModel;

    public AccountsPage(SmtpDispatcher dispatcher)
    {
        InitializeComponent();
        _dispatcher = dispatcher;
    }

    // ===== 发件账号 =====

    private void AddSender_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SenderEditDialog(_dispatcher) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            Vm?.Senders.Add(dlg.Result);
        }
    }

    private void EditSender_Click(object sender, RoutedEventArgs e)
    {
        if (SendersList.SelectedItem is not Sender s)
        {
            MessageBoxEx.Show("请先在账号列表中选择一项");
            return;
        }
        var dlg = new SenderEditDialog(_dispatcher, s) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            Vm?.Senders.Update(dlg.Result);
        }
    }

    private void UnbanSender_Click(object sender, RoutedEventArgs e)
    {
        if (SendersList.SelectedItem is not Sender s)
        {
            MessageBoxEx.Show("请先在账号列表中选择一项");
            return;
        }
        var wasBanned = s.Status == MailBot.Core.Senders.SenderStatus.Banned;
        Vm?.Senders.Unban(s);
        var msg = wasBanned
            ? $"账号 {s.Email} 已解除封禁"
            : $"账号 {s.Email} 当前状态不是 '封禁'（{s.Status}），无需解封";
        MessageBoxEx.Show(msg, "解除封禁", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DeleteSender_Click(object sender, RoutedEventArgs e)
    {
        if (SendersList.SelectedItem is not Sender s)
        {
            MessageBoxEx.Show("请先在账号列表中选择一项");
            return;
        }
        if (MessageBoxEx.ShowYesNo($"确定删除账号 {s.Email}？", "删除确认") == MessageBoxResult.Yes)
        {
            Vm?.Senders.Delete(s);
        }
    }

    private void EditSentToday_Click(object sender, RoutedEventArgs e)
    {
        if (SendersList.SelectedItem is not Sender s)
        {
            MessageBoxEx.Show("请先在账号列表中选择一项");
            return;
        }
        var prompt = $"账号 {s.Email}\n当前已发 {s.SentToday} 封 / 配额 {s.DailyQuota} 封\n\n输入新的\"已发\"数（0 = 重置）：";
        var input = Microsoft.VisualBasic.Interaction.InputBox(prompt, "编辑已发", s.SentToday.ToString());
        // 用户点 Cancel → InputBox 返回空字符串
        if (string.IsNullOrEmpty(input)) return;
        if (!int.TryParse(input, out var value) || value < 0)
        {
            MessageBoxEx.Show("请输入非负整数", "编辑已发", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Vm?.Senders.SetSentToday(s, value);
        MessageBoxEx.Show($"账号 {s.Email} 的已发数已设为 {value} 封", "编辑已发", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ===== 验证邮箱 =====

    private void AddVerifier_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var v = new VerifierAccount
        {
            Email = string.Empty,
            ImapHost = "imap.qq.com",
            ImapPort = 993,
            UseSsl = true,
            Username = string.Empty,
            PasswordEncrypted = Array.Empty<byte>(),
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var dlg = new VerifierEditDialog(Vm.Verifiers, v) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            Vm.Verifiers.SaveNew(v, dlg.Password);
        }
    }

    private void EditVerifier_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (VerifiersList.SelectedItem is not VerifierAccount v)
        {
            MessageBoxEx.Show("请先在验证邮箱列表中选择一项");
            return;
        }
        var dlg = new VerifierEditDialog(Vm.Verifiers, v) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true)
        {
            Vm.Verifiers.Update(v, dlg.Password);
        }
    }

    private void DeleteVerifier_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        if (VerifiersList.SelectedItem is not VerifierAccount v)
        {
            MessageBoxEx.Show("请先在验证邮箱列表中选择一项");
            return;
        }
        if (MessageBoxEx.ShowYesNo($"确定删除验证邮箱 {v.Email}？", "删除确认") == MessageBoxResult.Yes)
        {
            Vm.Verifiers.Delete(v.Id);
        }
    }
}