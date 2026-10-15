using System.Windows;
using System.Windows.Controls;
using MailBot.App.Controls;
using MailBot.App.ViewModels;
using MailBot.Core.Mail;
using MailBot.Core.Senders;
using MailBot.Core.Sending;
using MailBot.Core.Storage;
using Microsoft.Win32;

namespace MailBot.App.Views.Pages;

/// <summary>邮件撰写页：主题 + 富文本正文 + 附件。DataContext 继承自 MainViewModel。</summary>
public partial class ComposePage : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    private readonly SmtpDispatcher _dispatcher;
    private readonly SenderPool _senderPool;
    private readonly VerifierRepository _verifierRepo;

    /// <summary>富文本编辑器引用：MainWindow 的"保存配置"按钮在保存前需要 SyncNow() 同步 HTML。</summary>
    public Controls.HtmlEditor BodyEditorControl => BodyEditor;

    public ComposePage(SmtpDispatcher dispatcher, SenderPool senderPool, VerifierRepository verifierRepo)
    {
        InitializeComponent();
        _dispatcher = dispatcher;
        _senderPool = senderPool;
        _verifierRepo = verifierRepo;
    }

    private void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var path in dlg.FileNames)
        {
            Vm?.AddAttachmentCommand.Execute(path);
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (AttachmentList.SelectedItem is string path)
        {
            Vm?.RemoveAttachmentCommand.Execute(path);
        }
    }

    /// <summary>保存前同步富文本编辑器的当前 HTML 到 VM（避免 LostFocus 漏掉最近编辑）。</summary>
    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        BodyEditor.SyncNow();
    }

    /// <summary>ComposePage 不是 Window,MessageBox.Show(Window owner, ...) 不能直接传 this,先 GetWindow 拿主窗口。</summary>
    private void ShowMessage(string text, string caption, System.Windows.MessageBoxImage icon)
    {
        var owner = System.Windows.Window.GetWindow(this) ?? Application.Current.MainWindow;
        MessageBoxEx.Show(owner, text, caption, MessageBoxButton.OK, icon);
    }

    /// <summary>
    /// 测试发送：把当前主题/正文/附件装一封预览邮件发出去,
    /// 走与正式发送相同的 MailBuilder 路径,不计入配额、不参与 IMAP 验证。
    ///
    /// 收件人 = 首个启用的验证邮箱——用户在自己已经登录 IMAP 的收件箱立刻能看到效果,
    /// 也顺带验证了发件账号 SMTP 配置是否正确（如果发件账号也配错,会弹 SMTP 错误）。
    /// 没配验证邮箱 → 提示去账号页配,而不是回退到"发件人本人"（那样用户得多登一个邮箱才能看）。
    /// </summary>
    private async void TestSend_Click(object sender, RoutedEventArgs e)
    {
        // 1. 先把富文本编辑器的当前 HTML 拉回 VM,避免漏掉最近编辑
        BodyEditor.SyncNow();

        var vm = Vm;
        if (vm is null) return;

        // 2. 模板校验(与 RunAsync 走同一 MailTemplate.IsValid)
        var template = new MailTemplate
        {
            Subject = vm.Subject,
            HtmlBody = vm.HtmlBody,
            AttachmentPaths = vm.AttachmentPaths.ToList(),
        };
        if (!template.IsValid(out var error))
        {
            ShowMessage(
                $"模板无效：{error}",
                "测试发送",
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        // 3. 取首个启用的验证邮箱——测试发送的"收件人"。
        // 不用随机:用户改完模板再点测试,应该发到同一个验证邮箱(确定性更友好)。
        var verifiers = _verifierRepo.ListEnabled();
        var verifier = verifiers.FirstOrDefault();
        if (verifier is null)
        {
            ShowMessage(
                "未配置可用的验证邮箱。\n\n请先到「账号」页 → 「验证邮箱」分组添加并启用至少一个验证邮箱。\n\n" +
                "测试发送的邮件会发到这个验证邮箱,你需要登录该邮箱才能查看效果。",
                "测试发送",
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        // 4. 取首个可用发件账号(走真实 SMTP 鉴权)——逻辑与 SendEngine 一致
        var account = _senderPool.PickBestSender(DateTime.Now);
        if (account is null)
        {
            ShowMessage(
                "无可用发件账号（可能全部冷却或配额已用尽）。请先在「账号」页检查状态。",
                "测试发送",
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        // 5. 防连点:按钮临时禁用 + 改文案
        if (sender is System.Windows.Controls.Button btn)
        {
            btn.IsEnabled = false;
            btn.Content = "发送中...";
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var msg = MailBuilder.BuildTestSend(account, template, verifier.Email);
            var result = await _dispatcher.SendAsync(account, msg, System.Threading.CancellationToken.None);
            sw.Stop();

            if (result.IsSuccess)
            {
                vm.StatusBar = $"测试发送 → {verifier.Email}（发件账号 {account.Email}，{sw.ElapsedMilliseconds} ms）";
                MailBot.App.Logging.LogBus.Instance.Emit(
                    MailBot.Core.Logging.LogLevel.Success,
                    $"测试发送 → {verifier.Email}（发件账号 {account.Email}，{sw.ElapsedMilliseconds} ms）",
                    account: account.Email);
            }
            else
            {
                ShowMessage(
                    $"测试发送失败：{result.Category}\n{result.ErrorMessage}",
                    "测试发送",
                    System.Windows.MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            ShowMessage(
                $"测试发送异常：{ex.Message}",
                "测试发送",
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            if (sender is System.Windows.Controls.Button btn2)
            {
                btn2.IsEnabled = true;
                btn2.Content = "测试发送";
            }
        }
    }
}