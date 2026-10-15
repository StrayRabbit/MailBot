using MailBot.Core.Models;
using MailBot.Core.Senders;
using Xunit;

namespace MailBot.Tests.Senders;

public class SenderPoolTests
{
    private static Sender Sender(
        long id, string email, string status,
        int dailyQuota = 50, int sentToday = 0,
        int consecutiveFailures = 0, string? cooldownUntil = null)
        => new()
        {
            Id = id,
            Email = email,
            SmtpHost = "smtp.example.com",
            SmtpPort = 465,
            UseSsl = true,
            Username = email,
            PasswordEncrypted = Array.Empty<byte>(),
            DailyQuota = dailyQuota,
            SentToday = sentToday,
            QuotaResetDate = "2026-09-25",
            ConsecutiveFailures = consecutiveFailures,
            Status = status,
            CooldownUntil = cooldownUntil,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    [Fact]
    public void PickBestSender_Empty_ReturnsNull()
    {
        var picked = SenderPool.PickBestSender(Array.Empty<Sender>(), DateTime.Now);
        Assert.Null(picked);
    }

    [Fact]
    public void PickBestSender_ExcludesBanned()
    {
        var senders = new[]
        {
            Sender(1, "a@x.com", SenderStatus.Banned),
        };
        Assert.Null(SenderPool.PickBestSender(senders, DateTime.Now));
    }

    [Fact]
    public void PickBestSender_ExcludesCoolingInCooldown()
    {
        var future = DateTime.Now.AddHours(1).ToString("o");
        var senders = new[]
        {
            Sender(1, "a@x.com", SenderStatus.Cooling, cooldownUntil: future),
        };
        Assert.Null(SenderPool.PickBestSender(senders, DateTime.Now));
    }

    [Fact]
    public void PickBestSender_IncludesCoolingAfterCooldown()
    {
        var past = DateTime.Now.AddHours(-1).ToString("o");
        var senders = new[]
        {
            Sender(1, "a@x.com", SenderStatus.Cooling, cooldownUntil: past),
        };
        var picked = SenderPool.PickBestSender(senders, DateTime.Now);
        Assert.NotNull(picked);
        Assert.Equal("a@x.com", picked!.Email);
    }

    [Fact]
    public void PickBestSender_ExhaustedQuota()
    {
        var senders = new[]
        {
            Sender(1, "a@x.com", SenderStatus.Normal, dailyQuota: 50, sentToday: 50),
        };
        Assert.Null(SenderPool.PickBestSender(senders, DateTime.Now));
    }

    [Fact]
    public void PickBestSender_PrefersMostRemaining()
    {
        var senders = new[]
        {
            Sender(1, "low@x.com", SenderStatus.Normal, dailyQuota: 100, sentToday: 80),
            Sender(2, "high@x.com", SenderStatus.Normal, dailyQuota: 100, sentToday: 10),
        };
        var picked = SenderPool.PickBestSender(senders, DateTime.Now);
        Assert.NotNull(picked);
        Assert.Equal("high@x.com", picked!.Email);
    }

    [Fact]
    public void PickBestSender_SkipsBannedEvenIfNormalQuota()
    {
        var senders = new[]
        {
            Sender(1, "banned@x.com", SenderStatus.Banned, dailyQuota: 100),
            Sender(2, "ok@x.com", SenderStatus.Normal, dailyQuota: 100, sentToday: 50),
        };
        var picked = SenderPool.PickBestSender(senders, DateTime.Now);
        Assert.NotNull(picked);
        Assert.Equal("ok@x.com", picked!.Email);
    }

    [Fact]
    public void GetEarliestRetryTime_AllAvailable_ReturnsNow()
    {
        var senders = new[]
        {
            Sender(1, "ok@x.com", SenderStatus.Normal, dailyQuota: 100, sentToday: 0),
        };
        var now = DateTime.Now;
        var earliest = SenderPool.GetEarliestRetryTime(senders, now);
        Assert.Null(earliest); // 全部可用 → null（表示立即可发）
    }

    [Fact]
    public void GetEarliestRetryTime_OneCooling_ReturnsCooldownUntil()
    {
        var now = DateTime.Now;
        var cooldownEnd = now.AddMinutes(45);
        var senders = new[]
        {
            Sender(1, "cool@x.com", SenderStatus.Cooling, cooldownUntil: cooldownEnd.ToString("o")),
        };
        var earliest = SenderPool.GetEarliestRetryTime(senders, now);
        Assert.NotNull(earliest);
        // 允许秒级误差
        Assert.True((earliest!.Value - cooldownEnd).TotalSeconds < 5);
    }

    [Fact]
    public void GetEarliestRetryTime_PicksMinOfMultipleSenders()
    {
        var now = DateTime.Now;
        var cooldownEnd = now.AddMinutes(45);
        var quotaReset = now.Date.AddDays(1);
        var senders = new[]
        {
            // 冷却 45min 后的账号
            Sender(1, "cool@x.com", SenderStatus.Cooling, cooldownUntil: cooldownEnd.ToString("o")),
            // 配额已用尽（次日重置，更远）
            Sender(2, "quota@x.com", SenderStatus.Normal, dailyQuota: 10, sentToday: 10),
            // 封禁账号（不参与计算）
            Sender(3, "banned@x.com", SenderStatus.Banned),
        };
        var earliest = SenderPool.GetEarliestRetryTime(senders, now);
        Assert.NotNull(earliest);
        // 应该返回冷却到期时间（45min 后），而非配额重置（明天 00:00）
        Assert.True((earliest!.Value - cooldownEnd).TotalSeconds < 5);
        // 也不应取配额重置的更晚时间
        Assert.True(earliest.Value < quotaReset);
    }

    [Fact]
    public void GetEarliestRetryTime_OnlyBanned_ReturnsNull()
    {
        var senders = new[]
        {
            Sender(1, "banned@x.com", SenderStatus.Banned),
        };
        var earliest = SenderPool.GetEarliestRetryTime(senders, DateTime.Now);
        // 封禁不参与"自动解锁"，返回 null 表示需要人工介入
        Assert.Null(earliest);
    }
}