using MailBot.Core.Verification;
using Xunit;

namespace MailBot.Tests.Verification;

public class BatchIdFormatterTests
{
    [Fact]
    public void Short_NullOrEmpty_ReturnsPlaceholder()
    {
        Assert.Equal("????", BatchIdFormatter.Short(""));
        Assert.Equal("????", BatchIdFormatter.Short(string.Empty));
        // null 输入按 .NET 约定：Length 抛 NRE；调用方应保证非空。这里不在范围。
    }

    [Fact]
    public void Short_TakesLastFourAfterFinalDash()
    {
        Assert.Equal("7bff", BatchIdFormatter.Short("2026-09-26-001-7bff"));
        Assert.Equal("abcd", BatchIdFormatter.Short("x-y-z-abcd"));
    }

    [Fact]
    public void Short_NoDash_TakesLastFourOfWhole()
    {
        Assert.Equal("abcd", BatchIdFormatter.Short("abcd"));
        Assert.Equal("1234", BatchIdFormatter.Short("wxyz1234"));
        // 长度 == 4 时取全部
        Assert.Equal("wxyz", BatchIdFormatter.Short("wxyz"));
    }

    [Fact]
    public void Short_TailShorterThanFour_TakesWhole()
    {
        Assert.Equal("abc", BatchIdFormatter.Short("2026-09-26-abc"));
    }
}