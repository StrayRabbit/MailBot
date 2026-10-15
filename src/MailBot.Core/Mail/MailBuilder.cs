using MailBot.Core.Models;
using MimeKit;

namespace MailBot.Core.Mail;

/// <summary>
/// 把 Sender / 收件人列表 / MailTemplate 组装成 MimeMessage。
/// 设计文档 6.3 节：占位 To + 真正收件人放 BCC，由 SmtpClient.Send(...) 直接送。
/// </summary>
public static class MailBuilder
{
    /// <summary>[已过时] 兼容入口，保留以不破坏外部调用；新代码请直接调用 BuildForRecipients。</summary>
    [Obsolete("请使用 BuildForRecipients + 可选 verifierBcc 参数")]
    public static MimeMessage Build(Sender sender, MailTemplate template, IEnumerable<Recipient> recipients)
        => BuildForRecipients(sender, template, recipients);

    /// <summary>
    /// 给真实收件人装配邮件：Bcc 含真实收件人 + 可选的 verifier 邮箱。
    /// 合并发送：verifier 邮箱作为 BCC 与真实收件人同封送达，主题保持模板原样（不带 [batch=xxx] 前缀）。
    /// 合并前：BuildForVerifiers 单独发送带 [batch=] 前缀的邮件；合并后：删除了该方法（保留 [Obsolete] 私有骨架用于回退）。
    /// </summary>
    public static MimeMessage BuildForRecipients(
        Sender sender,
        MailTemplate template,
        IEnumerable<Recipient> recipients,
        IReadOnlyList<string>? verifierBcc = null)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(sender.Email));

        // 收件人放置策略（参考 AutoSendEmail 成功实现）：
        //   - 单收件人：直接放 msg.To，看起来像正常邮件，避免被反垃圾标记
        //   - 多收件人 BCC：第一个进 msg.To（MailKit 保留在头部），其余进 msg.Bcc
        //     （MailKit 自动剥离 Bcc 头 + 用 msg.To+msg.Bcc 作为信封 RCPT TO）
        var parsed = new List<MailboxAddress>();
        foreach (var r in recipients)
        {
            if (string.IsNullOrWhiteSpace(r.Email)) continue;
            try
            {
                var mailbox = MailboxAddress.Parse(r.Email.Trim());
                if (string.IsNullOrWhiteSpace(mailbox.Address) || !mailbox.Address.Contains('@')) continue;
                parsed.Add(mailbox);
            }
            catch
            {
                // 单个地址解析失败：跳过，让上游按单条失败处理
            }
        }

        if (parsed.Count == 1)
        {
            msg.To.Add(parsed[0]);
        }
        else if (parsed.Count > 1)
        {
            msg.To.Add(parsed[0]);
            for (int i = 1; i < parsed.Count; i++)
            {
                msg.Bcc.Add(parsed[i]);
            }
        }

        // verifier 进 BCC：与真实收件人同封送达，主题保持模板原样（不带 [batch=xxx] 前缀）
        if (verifierBcc != null)
        {
            foreach (var v in verifierBcc)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                try
                {
                    var mb = MailboxAddress.Parse(v.Trim());
                    if (!string.IsNullOrWhiteSpace(mb.Address) && mb.Address.Contains('@'))
                    {
                        msg.Bcc.Add(mb);
                    }
                }
                catch
                {
                    // 单个验证邮箱解析失败：跳过（视为无 verifier 降级由上游处理）
                }
            }
        }

        msg.Subject = StripControlChars(template.Subject);
        msg.Body = BuildBody(template);
        return msg;
    }

    /// <summary>
    /// 给验证邮箱单独装配邮件：[已过时] 合并发送后此方法不再调用。
    /// 保留方法骨架为 [Obsolete] internal 方法，仅供紧急回退用。
    /// 平时不调用、不被引用；编译期通过 Obsolete 警告提醒维护者不要在新代码中使用。
    /// </summary>
    [Obsolete("合并发送后请使用 BuildForRecipients + verifierBcc 参数；此方法仅作紧急回退用", false)]
    internal static MimeMessage? BuildForVerifiers(
        Sender sender,
        MailTemplate template,
        IReadOnlyList<string> verifierEmails,
        string batchTag)
    {
        if (verifierEmails.Count == 0) return null;

        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(sender.Email));

        foreach (var v in verifierEmails)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            try
            {
                var mb = MailboxAddress.Parse(v.Trim());
                if (!string.IsNullOrWhiteSpace(mb.Address) && mb.Address.Contains('@'))
                {
                    if (msg.To.Count == 0)
                    {
                        msg.To.Add(mb);
                    }
                    else
                    {
                        msg.Bcc.Add(mb);
                    }
                }
            }
            catch
            {
                // 单个验证邮箱解析失败：跳过
            }
        }

        if (msg.To.Count == 0 && msg.Bcc.Count == 0) return null;

        msg.Subject = StripControlChars($"[batch={batchTag}] {template.Subject}");
        msg.Body = BuildBody(template);
        return msg;
    }

    /// <summary>
    /// 给发件人本人装配预览邮件：复用模板的 Subject / Body / 附件装配，
    /// 但只装 To=sender.Email、不带 verifier BCC、不参与 IMAP 验证、不计入配额。
    /// 注入 X-MailBot-Test: 1 头供收件端用规则过滤测试邮件。
    /// </summary>
    /// <param name="sender">发件账号（SMTP 鉴权用）。</param>
    /// <param name="template">邮件模板。</param>
    /// <param name="recipientEmail">
    /// 收件人邮箱。默认 <c>sender.Email</c>（自发自收的便捷写法）；
    /// Compose 页"测试发送"通常传验证邮箱地址，让用户在自己已登录 IMAP 的收件箱看到。
    /// </param>
    public static MimeMessage BuildTestSend(
        Sender sender,
        MailTemplate template,
        string? recipientEmail = null)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(sender.Email));
        msg.To.Add(MailboxAddress.Parse(recipientEmail ?? sender.Email));
        msg.Headers.Add("X-MailBot-Test", "1");
        msg.Subject = StripControlChars(template.Subject);
        msg.Body = BuildBody(template);
        return msg;
    }

    private static string StripControlChars(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var buf = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\r' || c == '\n' || c == '\0' || (c < 0x20 && c != '\t') || c == 0x7F)
                continue;
            buf.Append(c);
        }
        return buf.ToString();
    }

    private static MimeEntity BuildBody(MailTemplate template)
    {
        var builder = new BodyBuilder
        {
            HtmlBody = template.HtmlBody,
        };
        foreach (var path in template.AttachmentPaths)
        {
            if (File.Exists(path))
            {
                builder.Attachments.Add(path);
            }
        }
        return builder.ToMessageBody();
    }

    /// <summary>从 recipients 提取真实收件地址，供 SmtpClient.Send(..., bcc, ct) 使用。</summary>
    public static List<MailboxAddress> ExtractBcc(IEnumerable<Recipient> recipients)
    {
        var list = new List<MailboxAddress>();
        foreach (var r in recipients)
        {
            if (string.IsNullOrWhiteSpace(r.Email)) continue;
            try
            {
                list.Add(MailboxAddress.Parse(r.Email.Trim()));
            }
            catch
            {
                // 单个地址解析失败：跳过，让上游按单条失败处理
            }
        }
        return list;
    }
}