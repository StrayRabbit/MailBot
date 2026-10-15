using System.Windows;
using MailBot.App.Controls;
using MailBot.App.ViewModels;
using MailBot.Core.Models;
using MailBot.Core.Security;

namespace MailBot.App.Views;

public partial class VerifierEditDialog : Window
{
    private readonly VerifiersViewModel _vm;
    private readonly VerifierAccount _v;

    /// <summary>编辑时原密码(解密后)。仅在 PasswordBox 留空时保留。</summary>
    private readonly string _existingPassword;

    public VerifierEditDialog(VerifiersViewModel vm, VerifierAccount v)
    {
        InitializeComponent();
        _vm = vm;
        _v = v;
        EmailBox.Text = v.Email;
        HostBox.Text = v.ImapHost;
        PortBox.Text = v.ImapPort.ToString();
        UsernameBox.Text = v.Username;
        SslCheck.IsChecked = v.UseSsl;

        // 编辑时回填授权码（DPAPI 解密）；新增时 _existingPassword 为空，下面自然不写。
        // 解密失败（其他用户/机器产生的 db）降级为留空，提示用户重新输入。
        try
        {
            _existingPassword = v.PasswordEncrypted.Length > 0
                ? CredentialProtector.Unprotect(v.PasswordEncrypted)
                : string.Empty;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            _existingPassword = string.Empty;
            Hint.Text = "⚠ 当前 Windows 用户无法解密该验证邮箱的原授权码。请重新输入并保存。";
        }
        PasswordBox.Password = _existingPassword;
    }

    public string Password => PasswordBox.Password;

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        Collect();
        var (ok, err) = await _vm.TestConnectionAsync(_v, Password, CancellationToken.None);
        MessageBoxEx.Show(this, ok ? "连接成功" : $"连接失败: {err}",
            "测试连接", MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Collect();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Collect()
    {
        _v.Email = EmailBox.Text.Trim();
        _v.ImapHost = HostBox.Text.Trim();
        _v.ImapPort = int.TryParse(PortBox.Text, out var p) ? p : 993;
        _v.Username = UsernameBox.Text.Trim();
        _v.UseSsl = SslCheck.IsChecked == true;

        // 授权码：留空表示沿用旧值（跟发件账号的语义对齐），有输入才覆盖。
        // 这样不会因为"啥也没改"把原密码误删，也不会让 DPAPI 解密失败时直接清空。
        var newPwd = PasswordBox.Password;
        if (!string.IsNullOrEmpty(newPwd) && newPwd != _existingPassword)
        {
            _v.PasswordEncrypted = CredentialProtector.Protect(newPwd);
        }
    }
}