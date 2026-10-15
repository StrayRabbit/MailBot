using MailBot.Core.Scheduling;
using Xunit;

namespace MailBot.Tests.Scheduling;

public class TimeWindowTests
{
    [Fact]
    public void SameDay_InsideWindow()
    {
        var w = new TimeWindow("09:00", "18:00");
        Assert.True(w.IsInWindow(new DateTime(2026, 9, 25, 10, 0, 0)));
        Assert.True(w.IsInWindow(new DateTime(2026, 9, 25, 9, 0, 0)));
        Assert.False(w.IsInWindow(new DateTime(2026, 9, 25, 18, 0, 0)));
        Assert.False(w.IsInWindow(new DateTime(2026, 9, 25, 8, 59, 59)));
    }

    [Fact]
    public void CrossDay_Window()
    {
        var w = new TimeWindow("22:00", "06:00");
        Assert.True(w.IsInWindow(new DateTime(2026, 9, 25, 23, 30, 0)));
        Assert.True(w.IsInWindow(new DateTime(2026, 9, 25, 2, 0, 0)));
        Assert.False(w.IsInWindow(new DateTime(2026, 9, 25, 10, 0, 0)));
        Assert.False(w.IsInWindow(new DateTime(2026, 9, 25, 21, 59, 59)));
    }

    [Fact]
    public void NextStart_BeforeTodayStart()
    {
        var w = new TimeWindow("09:00", "18:00");
        var now = new DateTime(2026, 9, 25, 7, 0, 0);
        var next = w.NextStart(now);
        Assert.Equal(new DateTime(2026, 9, 25, 9, 0, 0), next);
    }

    [Fact]
    public void NextStart_AfterTodayEnd()
    {
        var w = new TimeWindow("09:00", "18:00");
        var now = new DateTime(2026, 9, 25, 19, 0, 0);
        var next = w.NextStart(now);
        Assert.Equal(new DateTime(2026, 9, 26, 9, 0, 0), next);
    }

    [Fact]
    public void NextStart_InsideWindow_ReturnsNow()
    {
        var w = new TimeWindow("09:00", "18:00");
        var now = new DateTime(2026, 9, 25, 12, 0, 0);
        var next = w.NextStart(now);
        Assert.Equal(now, next);
    }

    [Fact]
    public void InvalidTimeString_Throws()
    {
        Assert.Throws<FormatException>(() => new TimeWindow("not-a-time", "18:00"));
    }
}