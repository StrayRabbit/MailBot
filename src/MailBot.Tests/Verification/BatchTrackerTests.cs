using MailBot.Core.Verification;
using Xunit;

namespace MailBot.Tests.Verification;

public class BatchTrackerTests
{
    private static BatchTracker New() =>
        new("2026-09-25-001-aaaa", DateTime.UtcNow, senderId: 1, recipientIds: new[] { 10L, 20L, 30L });

    [Fact]
    public void New_StartsPending()
    {
        Assert.True(New().IsPending);
        Assert.Equal(BatchState.Pending, New().State);
    }

    [Fact]
    public void MarkVerified_Transitions()
    {
        var b = New();
        b.MarkVerified();
        Assert.Equal(BatchState.Verified, b.State);
        Assert.NotNull(b.ResolvedAt);
        Assert.Equal("verified-by-imap", b.Resolution);
    }

    [Fact]
    public void IncrementCheckAttempts_Transitions()
    {
        var b = New();
        Assert.Equal(0, b.CheckAttempts);
        b.IncrementCheckAttempts();
        Assert.Equal(1, b.CheckAttempts);
        b.IncrementCheckAttempts();
        Assert.Equal(2, b.CheckAttempts);
    }

    [Fact]
    public void IncrementCheckAttempts_NoopAfterTerminal()
    {
        var b = New();
        b.MarkVerified();
        var before = b.CheckAttempts;
        b.IncrementCheckAttempts(); // 终态后静默忽略
        Assert.Equal(before, b.CheckAttempts);
    }

    [Fact]
    public void MarkUnconfirmed_Transitions()
    {
        var b = New();
        b.MarkUnconfirmed();
        Assert.Equal(BatchState.Unconfirmed, b.State);
        Assert.Equal("unconfirmed-timeout", b.Resolution);
    }

    [Fact]
    public void TerminalState_IsImmutable()
    {
        var b = New();
        b.MarkVerified();
        b.MarkUnconfirmed(); // 静默忽略
        Assert.Equal(BatchState.Verified, b.State);
        Assert.Equal("verified-by-imap", b.Resolution);
    }

    [Fact]
    public void Constructor_AcceptsInitialUnconfirmedAttempts()
    {
        var b = new BatchTracker("b1", DateTime.UtcNow, 1, new long[] { 1L }, initialUnconfirmedAttempts: 2);
        Assert.Equal(2, b.UnconfirmedAttempts);
    }

    [Fact]
    public void IncrementUnconfirmedAttempts_NoopAfterTerminal()
    {
        var b = New();
        b.MarkVerified();
        var before = b.UnconfirmedAttempts;
        b.IncrementUnconfirmedAttempts(); // 终态后静默忽略
        Assert.Equal(before, b.UnconfirmedAttempts);
    }

    [Fact]
    public void RecordChargedQuota_StoresValue()
    {
        var b = New();
        b.RecordChargedQuota(5);
        Assert.Equal(5, b.ChargedQuotaCount);
    }

    [Fact]
    public void RecordChargedQuota_OverwritesWithLatest()
    {
        var b = New();
        b.RecordChargedQuota(3);
        b.RecordChargedQuota(7);
        Assert.Equal(7, b.ChargedQuotaCount);
    }

    [Fact]
    public void RecordChargedQuota_ZeroOrNegative_Ignored()
    {
        var b = New();
        b.RecordChargedQuota(0);
        Assert.Equal(0, b.ChargedQuotaCount);
        b.RecordChargedQuota(-1);
        Assert.Equal(0, b.ChargedQuotaCount);
    }

    [Fact]
    public void RecordChargedQuota_NoopAfterTerminal()
    {
        var b = New();
        b.MarkVerified();
        b.RecordChargedQuota(10); // 终态后静默忽略
        Assert.Equal(0, b.ChargedQuotaCount);
    }
}