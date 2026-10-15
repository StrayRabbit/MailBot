using MailBot.Core.Mail;
using MailBot.Core.Models;
using MimeKit;
using Xunit;

namespace MailBot.Tests.Mail;

/// <summary>
/// "测试发送" 路径的装配验证：与正式发送（BuildForRecipients）走同一套
/// Subject / Body / 附件处理，但只装 To=sender.Email、不带 verifier BCC、
/// 注入 X-MailBot-Test: 1 头供收件端识别/过滤。
/// </summary>
public class MailBuilderTestSendTests
{
    private static Sender MakeSender(string email = "alice@example.com") => new()
    {
        Email = email,
        SmtpHost = "smtp.example.com",
        SmtpPort = 465,
        UseSsl = true,
        Username = "alice",
        PasswordEncrypted = Array.Empty<byte>(),
        DailyQuota = 50,
    };

    private static MailTemplate MakeTemplate() => new()
    {
        Subject = "邀请函",
        HtmlBody = "<p>你好</p>",
    };

    [Fact]
    public void BuildTestSend_ToContainsSenderEmail()
    {
        var msg = MailBuilder.BuildTestSend(MakeSender(), MakeTemplate());

        Assert.Single(msg.To);
        var to = Assert.IsType<MimeKit.MailboxAddress>(msg.To[0]);
        Assert.Equal("alice@example.com", to.Address);
    }

    [Fact]
    public void BuildTestSend_BccIsEmpty()
    {
        var msg = MailBuilder.BuildTestSend(MakeSender(), MakeTemplate());

        Assert.Empty(msg.Bcc);
    }

    [Fact]
    public void BuildTestSend_FromIsSenderEmail()
    {
        var msg = MailBuilder.BuildTestSend(MakeSender(), MakeTemplate());

        Assert.Single(msg.From);
        var from = Assert.IsType<MimeKit.MailboxAddress>(msg.From[0]);
        Assert.Equal("alice@example.com", from.Address);
    }

    [Fact]
    public void BuildTestSend_IncludesTestHeader()
    {
        var msg = MailBuilder.BuildTestSend(MakeSender(), MakeTemplate());

        // X-MailBot-Test: 1 — 收件端可用此头过滤测试邮件。
        Assert.Equal("1", msg.Headers["X-MailBot-Test"]);
    }

    [Fact]
    public void BuildTestSend_SubjectFromTemplate()
    {
        var t = MakeTemplate();
        t.Subject = "Hello 世界";

        var msg = MailBuilder.BuildTestSend(MakeSender(), t);

        Assert.Equal("Hello 世界", msg.Subject);
    }

    [Fact]
    public void BuildTestSend_SubjectStripsControlChars()
    {
        var t = MakeTemplate();
        t.Subject = "正常\r\n注入 Bcc: victim@x.com";

        var msg = MailBuilder.BuildTestSend(MakeSender(), t);

        // 与 BuildForRecipients 走同一 StripControlChars,纵深防御 header injection。
        Assert.NotNull(msg.Subject);
        Assert.DoesNotContain('\r', msg.Subject);
        Assert.DoesNotContain('\n', msg.Subject);
    }

    [Fact]
    public void BuildTestSend_HtmlBodyPresent()
    {
        var msg = MailBuilder.BuildTestSend(MakeSender(), MakeTemplate());

        Assert.NotNull(msg.Body);
        // MimeMessage.Body 是 TextPart(Html),内容应包含模板正文片段。
        var textPart = Assert.IsAssignableFrom<MimeKit.TextPart>(msg.Body);
        Assert.Contains("你好", textPart.Text);
    }

    [Fact]
    public void BuildTestSend_WithExplicitRecipient_ToIsRecipientEmail()
    {
        // Compose 页"测试发送"用法:发件人是 sender,收件人是验证邮箱(便于在自己 IMAP 收件箱看)
        var msg = MailBuilder.BuildTestSend(
            MakeSender("alice@example.com"),
            MakeTemplate(),
            recipientEmail: "verifier@example.com");

        Assert.Single(msg.From);
        var from = Assert.IsType<MimeKit.MailboxAddress>(msg.From[0]);
        Assert.Equal("alice@example.com", from.Address);

        Assert.Single(msg.To);
        var to = Assert.IsType<MimeKit.MailboxAddress>(msg.To[0]);
        Assert.Equal("verifier@example.com", to.Address);

        // From 不受 recipientEmail 影响
        Assert.Empty(msg.Bcc);
    }

    [Fact]
    public void BuildTestSend_WithoutRecipient_FallsBackToSenderEmail()
    {
        // 不传 recipientEmail → 保持旧行为:To = sender.Email
        var msg = MailBuilder.BuildTestSend(MakeSender("alice@example.com"), MakeTemplate());

        var to = Assert.IsType<MimeKit.MailboxAddress>(msg.To[0]);
        Assert.Equal("alice@example.com", to.Address);
    }
}