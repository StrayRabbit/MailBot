using MailBot.Core.Verification;
using Xunit;

namespace MailBot.Tests.Verification;

public class VerifierConfigCooldownTests
{
    [Theory]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(4, 480)]
    [InlineData(5, 960)]
    [InlineData(6, 1440)] // 1920 → 截到 cap
    [InlineData(7, 1440)]
    [InlineData(20, 1440)]
    public void ComputeCooldownMinutes_DoublesAndCaps(int attempts, int expectedMinutes)
    {
        var cfg = new VerifierConfig
        {
            UnconfirmedCooldownMinutes = 60,
            CooldownDoublingCapMinutes = 1440,
        };
        var m = typeof(VerifierConfig).GetMethod("ComputeCooldownMinutes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(m);
        var result = (int)m!.Invoke(cfg, new object[] { attempts })!;
        Assert.Equal(expectedMinutes, result);
    }

    [Fact]
    public void ComputeCooldownMinutes_NoOverflow_ForLargeAttempts()
    {
        // attempts 太大时 1L << shift 会溢出 long；用 cap 截断
        var cfg = new VerifierConfig
        {
            UnconfirmedCooldownMinutes = 60,
            CooldownDoublingCapMinutes = 1440,
        };
        var m = typeof(VerifierConfig).GetMethod("ComputeCooldownMinutes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var result = (int)m!.Invoke(cfg, new object[] { 100 })!;
        Assert.Equal(1440, result); // cap
    }

    [Fact]
    public void ComputeCooldownMinutes_AttemptsZero_ReturnsBase()
    {
        var cfg = new VerifierConfig
        {
            UnconfirmedCooldownMinutes = 60,
            CooldownDoublingCapMinutes = 1440,
        };
        var m = typeof(VerifierConfig).GetMethod("ComputeCooldownMinutes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var result = (int)m!.Invoke(cfg, new object[] { 0 })!;
        Assert.Equal(60, result);
    }
}