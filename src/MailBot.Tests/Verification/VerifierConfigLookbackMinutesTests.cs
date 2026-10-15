using MailBot.Core.Verification;
using Xunit;

namespace MailBot.Tests.Verification;

public class VerifierConfigLookbackMinutesTests
{
    [Fact]
    public void Default_LookbackMinutes_Is_Five()
    {
        var cfg = new VerifierConfig();
        Assert.Equal(5, cfg.LookbackMinutes);
    }

    [Fact]
    public void Validate_Throws_When_LookbackMinutes_Less_Than_One()
    {
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, LookbackMinutes = 0 };
        var ex = Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        Assert.Contains("verification_lookback_minutes", ex.Message);
    }

    [Fact]
    public void Validate_Throws_When_LookbackMinutes_Negative()
    {
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, LookbackMinutes = -1 };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Validate_Throws_When_LookbackMinutes_Too_Small_For_RecheckInterval()
    {
        // LookbackMinutes=2 (120s) < RecheckIntervalSeconds=180s
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, LookbackMinutes = 2 };
        var ex = Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        Assert.Contains("verification_lookback_minutes", ex.Message);
    }

    [Fact]
    public void Validate_OK_When_LookbackMinutes_Equals_RecheckInterval_In_Minutes()
    {
        // LookbackMinutes=3 (180s) == RecheckIntervalSeconds=180s, 边界条件 OK
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, LookbackMinutes = 3 };
        cfg.Validate(); // 不抛
    }

    [Fact]
    public void Validate_OK_When_LookbackMinutes_Greater_Than_RecheckInterval()
    {
        // LookbackMinutes=5 (300s) > RecheckIntervalSeconds=180s, OK
        var cfg = new VerifierConfig { RecheckIntervalSeconds = 180, LookbackMinutes = 5 };
        cfg.Validate(); // 不抛
    }
}