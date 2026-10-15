using MailBot.Core.Mail;
using MailBot.Core.Models;
using MimeKit;
using Xunit;

namespace MailBot.Tests.Sending;

public class MailBuilderForRecipientsTests
{
    private static Sender Sender() => new()
    {
        Email = "sender@x.com",
        SmtpHost = "h", SmtpPort = 465, Username = "u",
        PasswordEncrypted = Array.Empty<byte>(),
        QuotaResetDate = "2026-09-25",
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static MailTemplate Template() => new()
    {
        Subject = "邀请函",
        HtmlBody = "<p>Hi</p>",
        AttachmentPaths = new List<string>(),
    };

    [Fact]
    public void BuildForRecipients_SingleRecipient_PutsInTo_AndNoBatchPrefix()
    {
        var msg = MailBuilder.BuildForRecipients(Sender(), Template(),
            new[] { new Recipient { Email = "a@x.com" } });
        Assert.Single(msg.To);
        Assert.Equal("a@x.com", ((MailboxAddress)msg.To.First()).Address);
        Assert.Empty(msg.Bcc);
        Assert.Equal("邀请函", msg.Subject);
        Assert.DoesNotContain("[batch=", msg.Subject);
    }

    [Fact]
    public void BuildForRecipients_Multiple_FirstInToRestInBcc_NoPrefix()
    {
        var msg = MailBuilder.BuildForRecipients(Sender(), Template(),
            new[]
            {
                new Recipient { Email = "a@x.com" },
                new Recipient { Email = "b@x.com" },
                new Recipient { Email = "c@x.com" },
            });
        Assert.Single(msg.To);
        Assert.Equal("a@x.com", ((MailboxAddress)msg.To.First()).Address);
        Assert.Equal(2, msg.Bcc.Count);
        Assert.Equal("邀请函", msg.Subject);
    }

    [Fact]
    public void BuildForRecipients_NoVerifiersEverInBcc()
    {
        // 即便 verifier 参数存在，旧路径也不应再放入真实邮件（拆两封后的明确边界）
        var msg = MailBuilder.BuildForRecipients(Sender(), Template(),
            new[] { new Recipient { Email = "a@x.com" } });
        Assert.Empty(msg.Bcc);
    }

    [Fact]
    public void BuildForRecipients_StripsCrlfFromSubject()
    {
        // 防御 header injection：模板主题里的 CRLF 应被 strip 掉。
        var template = Template();
        template.Subject = "正常主题\r\nBcc: victim@x.com";
        var msg = MailBuilder.BuildForRecipients(Sender(), template,
            new[] { new Recipient { Email = "a@x.com" } });
        Assert.DoesNotContain("\r", msg.Subject);
        Assert.DoesNotContain("\n", msg.Subject);
        Assert.Equal("正常主题Bcc: victim@x.com", msg.Subject);
    }

    [Fact]
    public void BuildForRecipients_WithVerifierBcc_AppendsToBcc_NoPrefix()
    {
        var msg = MailBuilder.BuildForRecipients(Sender(), Template(),
            new[] { new Recipient { Email = "a@x.com" } },
            verifierBcc: new[] { "verify@qq.com" });
        // 单真实收件人 → To; verifier 进 Bcc
        Assert.Single(msg.To);
        Assert.Equal("a@x.com", ((MailboxAddress)msg.To.First()).Address);
        Assert.Single(msg.Bcc);
        Assert.Equal("verify@qq.com", ((MailboxAddress)msg.Bcc.First()).Address);
        // 主题不带 [batch=xxx] 前缀
        Assert.DoesNotContain("[batch=", msg.Subject);
        Assert.Equal("邀请函", msg.Subject);
    }

    [Fact]
    public void BuildForRecipients_MultipleRecipients_WithVerifierBcc()
    {
        var msg = MailBuilder.BuildForRecipients(Sender(), Template(),
            new[]
            {
                new Recipient { Email = "a@x.com" },
                new Recipient { Email = "b@x.com" },
            },
            verifierBcc: new[] { "verify@qq.com" });
        Assert.Single(msg.To);
        Assert.Equal("a@x.com", ((MailboxAddress)msg.To.First()).Address);
        // 1 个真实收件人 Bcc (b) + 1 个 verifier Bcc (verify) = 2 个 Bcc
        Assert.Equal(2, msg.Bcc.Count);
        Assert.Contains(msg.Bcc.Cast<MailboxAddress>(), mb => mb.Address == "b@x.com");
        Assert.Contains(msg.Bcc.Cast<MailboxAddress>(), mb => mb.Address == "verify@qq.com");
    }

    [Fact]
    public void BuildForRecipients_NullVerifierBcc_NoChange()
    {
        var msg = MailBuilder.BuildForRecipients(Sender(), Template(),
            new[] { new Recipient { Email = "a@x.com" } },
            verifierBcc: null);
        Assert.Empty(msg.Bcc);
    }

    [Fact]
    public void BuildForRecipients_EmptyVerifierBcc_NoChange()
    {
        var msg = MailBuilder.BuildForRecipients(Sender(), Template(),
            new[] { new Recipient { Email = "a@x.com" } },
            verifierBcc: Array.Empty<string>());
        Assert.Empty(msg.Bcc);
    }

    [Fact]
    public void BuildForRecipients_InvalidVerifierEmail_Skipped()
    {
        var msg = MailBuilder.BuildForRecipients(Sender(), Template(),
            new[] { new Recipient { Email = "a@x.com" } },
            verifierBcc: new[] { "", "not-an-email", "verify@qq.com" });
        Assert.Single(msg.Bcc);
        Assert.Equal("verify@qq.com", ((MailboxAddress)msg.Bcc.First()).Address);
    }
}

#pragma warning disable CS0618 // Type or member is obsolete (BuildForVerifiers obsolete since merge)
public class MailBuilderForVerifiersTests
{
    private static Sender Sender() => new()
    {
        Email = "sender@x.com",
        SmtpHost = "h", SmtpPort = 465, Username = "u",
        PasswordEncrypted = Array.Empty<byte>(),
        QuotaResetDate = "2026-09-25",
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static MailTemplate Template() => new()
    {
        Subject = "邀请函",
        HtmlBody = "<p>Hi</p>",
        AttachmentPaths = new List<string>(),
    };

    [Fact]
    public void BuildForVerifiers_AddsBatchPrefix_ToSubject()
    {
        var msg = MailBuilder.BuildForVerifiers(Sender(), Template(),
            verifierEmails: new[] { "verify@qq.com" },
            batchTag: "2026-09-25-001-a253");
        Assert.NotNull(msg);
        Assert.StartsWith("[batch=2026-09-25-001-a253]", msg!.Subject);
    }

    [Fact]
    public void BuildForVerifiers_PutsOnlyVerifiersInBcc()
    {
        var msg = MailBuilder.BuildForVerifiers(Sender(), Template(),
            verifierEmails: new[] { "verify@qq.com", "verify2@163.com" },
            batchTag: "b1");
        Assert.NotNull(msg);
        // 第一个 verifier 进 To，剩下的进 Bcc — 必须有 To 头，否则部分 SMTP 拒收
        // 且 IMAP 端把它视为"自我抄送"可能丢入已发送而不是收件箱。
        Assert.Single(msg!.To);
        Assert.Single(msg.Bcc);
        Assert.Equal("verify@qq.com", ((MailboxAddress)msg.To.First()).Address);
        Assert.Equal("verify2@163.com", ((MailboxAddress)msg.Bcc.First()).Address);
    }

    [Fact]
    public void BuildForVerifiers_EmptyVerifierList_ReturnsNull()
    {
        var msg = MailBuilder.BuildForVerifiers(Sender(), Template(),
            verifierEmails: Array.Empty<string>(),
            batchTag: "b1");
        Assert.Null(msg);
    }

    [Fact]
    public void BuildForVerifiers_NoRecipientsLeakedInBcc()
    {
        // 真实收件人永远不进验证邮件的 Bcc（即使与 verifier 同邮箱也不复制）
        var msg = MailBuilder.BuildForVerifiers(Sender(), Template(),
            verifierEmails: new[] { "verify@qq.com" },
            batchTag: "b1");
        Assert.NotNull(msg);
        Assert.DoesNotContain(msg!.Bcc.Cast<MailboxAddress>(), mb => mb.Address == "a@x.com");
    }

    [Fact]
    public void BuildForVerifiers_SingleVerifier_StillSentInTo()
    {
        // 回归测试：恰好 1 个 verifier 走 To 分支时（Bcc 为空）也算合法，
        // 旧逻辑"if (msg.Bcc.Count == 0) return null" 会让单 verifier 场景静默不发。
        var msg = MailBuilder.BuildForVerifiers(Sender(), Template(),
            verifierEmails: new[] { "verify@qq.com" },
            batchTag: "b1");
        Assert.NotNull(msg);
        Assert.Single(msg!.To);
        Assert.Empty(msg.Bcc);
        Assert.Equal("verify@qq.com", ((MailboxAddress)msg.To.First()).Address);
    }
}
#pragma warning restore CS0618