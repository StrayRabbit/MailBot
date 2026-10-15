using MailBot.Core.Mail;
using Xunit;

namespace MailBot.Tests.Mail;

public class MailTemplateTests
{
    [Fact]
    public void IsValid_NormalTemplate_ReturnsTrue()
    {
        var t = new MailTemplate { Subject = "邀请函", HtmlBody = "<p>Hi</p>" };
        Assert.True(t.IsValid(out _));
    }

    [Fact]
    public void IsValid_EmptySubject_ReturnsFalse()
    {
        var t = new MailTemplate { Subject = "", HtmlBody = "<p>Hi</p>" };
        Assert.False(t.IsValid(out var err));
        Assert.Contains("主题", err);
    }

    [Fact]
    public void IsValid_SubjectWithCrlf_ReturnsFalse()
    {
        var t = new MailTemplate
        {
            Subject = "正常主题\r\nBcc: victim@x.com",
            HtmlBody = "<p>Hi</p>",
        };
        Assert.False(t.IsValid(out var err));
        Assert.Contains("控制字符", err);
    }

    [Fact]
    public void IsValid_SubjectWithNul_ReturnsFalse()
    {
        var t = new MailTemplate
        {
            Subject = "正常主题\0hidden",
            HtmlBody = "<p>Hi</p>",
        };
        Assert.False(t.IsValid(out var err));
        Assert.Contains("控制字符", err);
    }

    [Fact]
    public void IsValid_HtmlWithNul_ReturnsFalse()
    {
        var t = new MailTemplate
        {
            Subject = "正常",
            HtmlBody = "<p>Hi</p>\0tail",
        };
        Assert.False(t.IsValid(out var err));
        Assert.Contains("NUL", err);
    }

    [Fact]
    public void IsValid_HtmlWithNewline_AllowsBecauseNewlinesAreLegitimate()
    {
        var t = new MailTemplate
        {
            Subject = "正常",
            HtmlBody = "<p>第一段</p>\n<p>第二段</p>\r\n<p>第三段</p>",
        };
        Assert.True(t.IsValid(out _));
    }
}