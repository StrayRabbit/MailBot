using MailBot.Core.Mail;
using MailBot.Core.Models;
using MailBot.Core.Security;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MailBot.Core.Sending;

/// <summary>
/// 单次 SMTP 发送结果分类。设计文档 6.5 节。
/// </summary>
public enum SmtpErrorCategory
{
    /// <summary>整批成功。</summary>
    Success,
    /// <summary>服务端临时不可用，整批失败 → 账号 cooling。</summary>
    TemporaryFailure,
    /// <summary>认证失败 → 账号 banned。</summary>
    AuthFailure,
    /// <summary>被识别为垃圾邮件 → 账号 cooling（更长）。</summary>
    SpamDetected,
    /// <summary>网络/连接问题 → 重试一次，再失败进入 cooling。</summary>
    NetworkError,
    /// <summary>未知错误 → 重试一次。</summary>
    Unknown,
}

/// <summary>
/// 单次 SMTP 发送结果。
/// </summary>
public sealed class SmtpSendResult
{
    public bool IsSuccess => Category == SmtpErrorCategory.Success;
    public SmtpErrorCategory Category { get; set; }
    public string? ErrorMessage { get; set; }
    public int StatusCode { get; set; }

    /// <summary>本次发送耗时（毫秒），记录到 UI 日志。</summary>
    public int DurationMs { get; set; }
}

/// <summary>
/// 单次 SMTP 发送：建立连接、鉴权、发送 BCC 批次、断连。
/// 错误码 → 分类的映射遵循设计文档 6.5。
///
/// 非 sealed：单测可派生 fake 替换 SendAsync。生产代码直接 new SmtpDispatcher()。
/// </summary>
public class SmtpDispatcher
{
    /// <summary>为单个账号建立 SMTP 连接并发送 BCC 批次。</summary>
    public async Task<SmtpSendResult> SendBatchAsync(
        Sender sender,
        MailTemplate template,
        IEnumerable<Recipient> recipients,
        CancellationToken cancellationToken)
    {
        var recipientsList = recipients.ToList();
        var msg = MailBuilder.Build(sender, template, recipientsList);
        return await SendAsync(sender, msg, cancellationToken);
    }

    /// <summary>已构建好的 MimeMessage 直发：避免 SendEngine 重复 Build。virtual 以允许单测派生替换。</summary>
    public virtual async Task<SmtpSendResult> SendAsync(
        Sender sender,
        MimeMessage msg,
        CancellationToken cancellationToken)
    {
        // 校验：MailBuilder 已按"单收件人→To、多收件人→首个 To + 余下 Bcc"装配；
        // 若 To 和 Bcc 都为空（说明所有地址都没解析过），直接判失败。
        if (msg.To.Count == 0 && msg.Bcc.Count == 0)
        {
            return new SmtpSendResult
            {
                Category = SmtpErrorCategory.Unknown,
                ErrorMessage = "没有可解析的收件人地址",
            };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var client = CreateClient(sender);
        try
        {
            var socketOption = PickSocketOption(sender);
            await client.ConnectAsync(sender.SmtpHost, sender.SmtpPort, socketOption, cancellationToken);

            var password = CredentialProtector.Unprotect(sender.PasswordEncrypted);
            await client.AuthenticateAsync(sender.Username, password, cancellationToken);

            // 不再使用 envelope 重载——MailKit 会自动：
            //   1) 用 msg.To + msg.Cc + msg.Bcc 当信封 RCPT TO
            //   2) 从外发消息中剥离 msg.Bcc 头（BCC 语义）
            // 配合 MailBuilder 的"单 To / 多 To+rest Bcc"装配，收件人看到的是正常 To，
            // 不再有"占位 To + 真实 RCPT TO"这种被 QQ 反垃圾判为异常的 BCC 模式。
            await client.SendAsync(msg, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            sw.Stop();
            return new SmtpSendResult
            {
                Category = SmtpErrorCategory.Success,
                DurationMs = (int)sw.ElapsedMilliseconds,
            };
        }
        catch (MailKit.Security.AuthenticationException ex)
        {
            // 535 认证失败 → banned
            return new SmtpSendResult
            {
                Category = SmtpErrorCategory.AuthFailure,
                ErrorMessage = ex.Message,
                DurationMs = (int)sw.ElapsedMilliseconds,
            };
        }
        catch (MailKit.Net.Smtp.SmtpCommandException ex)
        {
            // 4xx/5xx 协议错误
            return ClassifySmtpError(ex, (int)sw.ElapsedMilliseconds);
        }
        catch (MailKit.Net.Smtp.SmtpProtocolException ex)
        {
            // 协议层异常（通常是网络/超时）
            return new SmtpSendResult
            {
                Category = SmtpErrorCategory.NetworkError,
                ErrorMessage = ex.Message,
                DurationMs = (int)sw.ElapsedMilliseconds,
            };
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return new SmtpSendResult
            {
                Category = SmtpErrorCategory.NetworkError,
                ErrorMessage = ex.Message,
                DurationMs = (int)sw.ElapsedMilliseconds,
            };
        }
        catch (TimeoutException ex)
        {
            return new SmtpSendResult
            {
                Category = SmtpErrorCategory.NetworkError,
                ErrorMessage = ex.Message,
                DurationMs = (int)sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            return new SmtpSendResult
            {
                Category = SmtpErrorCategory.Unknown,
                ErrorMessage = ex.Message,
                DurationMs = (int)sw.ElapsedMilliseconds,
            };
        }
    }

    private static SmtpSendResult ClassifySmtpError(MailKit.Net.Smtp.SmtpCommandException ex, int durationMs)
    {
        var status = ex.StatusCode;
        var cat = status switch
        {
            MailKit.Net.Smtp.SmtpStatusCode.ServiceNotAvailable or MailKit.Net.Smtp.SmtpStatusCode.MailboxBusy => SmtpErrorCategory.TemporaryFailure,
            MailKit.Net.Smtp.SmtpStatusCode.AuthenticationInvalidCredentials => SmtpErrorCategory.AuthFailure,
            MailKit.Net.Smtp.SmtpStatusCode.TransactionFailed => SmtpErrorCategory.SpamDetected,
            _ => SmtpErrorCategory.Unknown,
        };
        return new SmtpSendResult
        {
            Category = cat,
            StatusCode = (int)status,
            ErrorMessage = ex.Message,
            DurationMs = durationMs,
        };
    }

    /// <summary>仅做连接 + 鉴权测试，不发送邮件。供 UI "测试连接" 按钮使用。</summary>
    public async Task<(bool ok, string? error)> TestConnectionAsync(Sender sender, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient(sender);
            var socketOption = PickSocketOption(sender);
            await client.ConnectAsync(sender.SmtpHost, sender.SmtpPort, socketOption, cancellationToken);
            var password = CredentialProtector.Unprotect(sender.PasswordEncrypted);
            await client.AuthenticateAsync(sender.Username, password, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 按 SMTP 端口选择连接选项：465 走隐式 SSL，其他端口优先 STARTTLS（服务器不支持时降级为明文）。
    /// 注意：协议分发以端口为准，不再以 UseSsl 为准——避免「端口 25 + 勾 SSL」走隐式 SSL 而连不上。
    /// </summary>
    private static SecureSocketOptions PickSocketOption(Sender sender) => sender.SmtpPort == 465
        ? SecureSocketOptions.SslOnConnect
        : SecureSocketOptions.StartTlsWhenAvailable;

    /// <summary>
    /// 创建 SmtpClient，按需配置证书校验回调。仅在用户显式勾选「信任无效证书」时跳过校验。
    /// </summary>
    private static SmtpClient CreateClient(Sender sender)
    {
        var client = new SmtpClient();
        if (sender.TrustInvalidCertificate)
        {
            // 跳过主机名不匹配 / 自签名 / 证书链不完整等错误。仅适用于自建 / 虚拟空间邮箱。
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }
        return client;
    }
}