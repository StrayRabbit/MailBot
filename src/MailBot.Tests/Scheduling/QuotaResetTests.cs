using MailBot.Core.Scheduling;
using Xunit;

namespace MailBot.Tests.Scheduling;

public class QuotaResetTests
{
    [Fact]
    public void NeedsReset_Empty_True()
    {
        Assert.True(QuotaReset.NeedsReset(string.Empty, DateTime.Now));
    }

    [Fact]
    public void NeedsReset_OldDate_True()
    {
        Assert.True(QuotaReset.NeedsReset("2026-09-24", new DateTime(2026, 9, 25, 10, 0, 0)));
    }

    [Fact]
    public void NeedsReset_SameDate_False()
    {
        Assert.False(QuotaReset.NeedsReset("2026-09-25", new DateTime(2026, 9, 25, 10, 0, 0)));
    }

    [Fact]
    public void NeedsReset_InvalidString_True()
    {
        Assert.True(QuotaReset.NeedsReset("not-a-date", DateTime.Now));
    }

    [Fact]
    public void Today_Format()
    {
        Assert.Equal("2026-09-25", QuotaReset.Today(new DateTime(2026, 9, 25, 23, 0, 0)));
    }

    [Fact]
    public void NextReset_IsNextMidnight()
    {
        var now = new DateTime(2026, 9, 25, 10, 0, 0);
        Assert.Equal(new DateTime(2026, 9, 26, 0, 0, 0), QuotaReset.NextReset(now));
    }
}