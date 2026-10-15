using System.Windows;
using MailBot.App.Controls;
using MailBot.Core.Models;
using MailBot.Core.Security;
using MailBot.Core.Sending;

namespace MailBot.App.Views;

/// <summary>
/// 发件账号新增/编辑对话框。设计文档 7.2 节关键交互 1：
/// 输入 SMTP 信息 → 测试连接 → 通过后保存。
/// </summary>
public partial class SenderEditDialog : Window
{
    private readonly SmtpDispatcher _dispatcher;
    private readonly Sender? _editing;

    public Sender Result { get; private set; } = new();

    public SenderEditDialog(SmtpDispatcher dispatcher, Sender? editing = null)
    {
        InitializeComponent();
        _dispatcher = dispatcher;
        _editing = editing;
        if (editing is not null)
        {
            Title = "编辑发件账号";
            EmailBox.Text = editing.Email;
            HostBox.Text = editing.SmtpHost;
            PortBox.Text = editing.SmtpPort.ToString();
            SslCheck.IsChecked = editing.UseSsl;
            TrustInvalidCertCheck.IsChecked = editing.TrustInvalidCertificate;
            UsernameBox.Text = editing.Username;
            QuotaBox.Text = editing.DailyQuota.ToString();
            // DPAPI CurrentUser 解密：密文必须由当前 Windows 用户在本机加密产生。
            // 从其他用户 / 其他机器拷贝过来的 db 在这里会抛 CryptographicException，
            // 降级为留空密码框，让用户重新输入并保存（用新密码重新 Protect 覆盖）。
            try
            {
                PasswordBox.Password = CredentialProtector.Unprotect(editing.PasswordEncrypted);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                PasswordBox.Password = string.Empty;
                Hint.Text = "⚠ 当前 Windows 用户无法解密该账号的原密码（账号由其他用户或机器加密入库）。请重新输入密码并保存。";
            }
            Result = editing;
        }
        else
        {
            Title = "新增发件账号";
        }
    }

    private bool ValidateAndFill(out string? error)
    {
        if (string.IsNullOrWhiteSpace(EmailBox.Text))
        {
            error = "邮箱不能为空";
            return false;
        }
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            error = "SMTP 服务器不能为空";
            return false;
        }
        if (!int.TryParse(PortBox.Text, out var port))
        {
            error = "端口格式错误";
            return false;
        }
        if (string.IsNullOrWhiteSpace(UsernameBox.Text))
        {
            error = "用户名不能为空";
            return false;
        }
        if (_editing is null && string.IsNullOrEmpty(PasswordBox.Password))
        {
            error = "密码不能为空";
            return false;
        }
        if (!int.TryParse(QuotaBox.Text, out var quota) || quota < 1)
        {
            error = "配额必须为正整数";
            return false;
        }
        Result = new Sender
        {
            Id = _editing?.Id ?? 0,
            Email = EmailBox.Text.Trim(),
            SmtpHost = HostBox.Text.Trim(),
            SmtpPort = port,
            UseSsl = SslCheck.IsChecked == true,
            TrustInvalidCertificate = TrustInvalidCertCheck.IsChecked == true,
            Username = UsernameBox.Text.Trim(),
            PasswordEncrypted = string.IsNullOrEmpty(PasswordBox.Password)
                ? (_editing?.PasswordEncrypted ?? Array.Empty<byte>())
                : CredentialProtector.Protect(PasswordBox.Password),
            DailyQuota = quota,
            SentToday = _editing?.SentToday ?? 0,
            QuotaResetDate = _editing?.QuotaResetDate ?? DateTime.Now.ToString("yyyy-MM-dd"),
            ConsecutiveFailures = _editing?.ConsecutiveFailures ?? 0,
            Status = _editing?.Status ?? "normal",
            CooldownUntil = _editing?.CooldownUntil,
            CreatedAt = _editing?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        error = null;
        return true;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateAndFill(out var error))
        {
            TestResult.Text = error;
            TestResult.Foreground = System.Windows.Media.Brushes.DarkRed;
            return;
        }
        TestResult.Text = "正在测试连接...";
        TestResult.Foreground = System.Windows.Media.Brushes.DarkBlue;
        try
        {
            var (ok, err) = await _dispatcher.TestConnectionAsync(Result, CancellationToken.None);
            TestResult.Text = ok ? "✓ 连接成功" : $"✗ 失败：{err}";
            TestResult.Foreground = ok
                ? System.Windows.Media.Brushes.DarkGreen
                : System.Windows.Media.Brushes.DarkRed;
        }
        catch (Exception ex)
        {
            TestResult.Text = $"✗ 异常：{ex.Message}";
            TestResult.Foreground = System.Windows.Media.Brushes.DarkRed;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateAndFill(out var error))
        {
            MessageBoxEx.Show(this, error, "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}