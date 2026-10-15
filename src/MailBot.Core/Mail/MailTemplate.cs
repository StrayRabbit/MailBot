namespace MailBot.Core.Mail;

/// <summary>
/// 一份邮件内容快照：主题、HTML 正文、附件路径。
/// 由 UI 层组装（编辑器的输出），传给 Core 层发送。
/// </summary>
public sealed class MailTemplate
{
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public List<string> AttachmentPaths { get; set; } = new();

    public bool IsValid(out string? error)
    {
        if (string.IsNullOrWhiteSpace(Subject))
        {
            error = "主题不能为空";
            return false;
        }
        if (string.IsNullOrWhiteSpace(HtmlBody))
        {
            error = "正文不能为空";
            return false;
        }
        // 纵深防御：阻止 header injection（CRLF / NUL 在 subject 中会被某些 SMTP 服务器解释为
        // 注入额外头如 "Bcc: victim@x.com"）。MimeKit 多数版本会拦截，但这里强制拒收更稳。
        if (ContainsControlChars(Subject))
        {
            error = "主题包含不允许的控制字符（换行 / 回车 / NUL 等）";
            return false;
        }
        if (ContainsControlChars(HtmlBody))
        {
            // HTML body 中的合法控制字符常见的有 tab；这里只拒 NUL 和 \r\n 防 SMTP 头注入误伤。
            if (HtmlBody.Contains('\0'))
            {
                error = "正文包含 NUL 字符";
                return false;
            }
        }
        error = null;
        return true;
    }

    private static bool ContainsControlChars(string s)
    {
        foreach (var c in s)
        {
            // \r \n \t 之外的 ASCII 控制字符 + DEL(0x7F)
            if (c == '\r' || c == '\n' || c == '\0' || (c < 0x20 && c != '\t') || c == 0x7F)
                return true;
        }
        return false;
    }
}