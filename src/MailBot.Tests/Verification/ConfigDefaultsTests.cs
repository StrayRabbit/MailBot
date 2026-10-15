using MailBot.Core.Storage;
using MailBot.Core.Verification;
using Xunit;

namespace MailBot.Tests.Verification;

public class ConfigDefaultsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Database _db;
    private readonly ConfigRepository _repo;

    public ConfigDefaultsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mailbot_test_{Guid.NewGuid():N}.db");
        _db = new Database(_dbPath);
        _db.EnsureSchema();
        _repo = new ConfigRepository(_db);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void Load_AllDefaults_WhenAppConfigEmpty()
    {
        var cfg = VerifierConfig.Load(_repo);
        Assert.True(cfg.Enabled);
        Assert.Equal(180, cfg.PollIntervalSeconds);
        Assert.Equal(180, cfg.RecheckIntervalSeconds);
        Assert.Equal(2, cfg.MaxPollAttempts);
        Assert.Equal(1, cfg.LookbackHours);
        Assert.Equal(5, cfg.LookbackMinutes);
        Assert.Equal(60, cfg.UnconfirmedCooldownMinutes);
        Assert.Equal(300, cfg.SendHoldOnUnverifiedSeconds);
    }

    [Fact]
    public void Load_OverridesFromAppConfig()
    {
        _repo.Set("verification_poll_interval_seconds", "10");
        _repo.Set("verification_enabled", "0");
        _repo.Set("verification_unconfirmed_cooldown_minutes", "90");
        _repo.Set("verification_lookback_hours", "24");
        _repo.Set("verification_max_poll_attempts", "3");
        _repo.Set("verification_recheck_interval_seconds", "60");
        var cfg = VerifierConfig.Load(_repo);
        Assert.False(cfg.Enabled);
        Assert.Equal(10, cfg.PollIntervalSeconds);
        Assert.Equal(60, cfg.RecheckIntervalSeconds);
        Assert.Equal(3, cfg.MaxPollAttempts);
        Assert.Equal(90, cfg.UnconfirmedCooldownMinutes);
        Assert.Equal(24, cfg.LookbackHours);
    }

    [Fact]
    public void Validate_OK_WhenSettingsLegal()
    {
        var cfg = new VerifierConfig
        {
            MaxPollAttempts = 2,
            LookbackHours = 10,
            UnconfirmedCooldownMinutes = 60,
            RecheckIntervalSeconds = 180,
            LookbackMinutes = 5,
        };
        cfg.Validate(); // 不抛
    }

    [Fact]
    public void Validate_Throws_WhenMaxPollAttemptsLessThanOne()
    {
        var cfg = new VerifierConfig { MaxPollAttempts = 0 };
        var ex = Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        Assert.Contains("verification_max_poll_attempts", ex.Message);
    }

    [Fact]
    public void Validate_Throws_WhenLookbackHoursNegative()
    {
        var cfg = new VerifierConfig { LookbackHours = -1 };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }
}