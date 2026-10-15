using MailBot.Core.Models;
using MailBot.Core.Senders;
using MailBot.Core.Sending;
using Xunit;

namespace MailBot.Tests.Sending;

/// <summary>
/// SenderFailureClassifier 是从 SendEngine 抽出的纯函数:
/// 输入 (Sender 当前态 + SmtpSendResult + AppConfig) → 输出 (新状态/冷却/连续失败计数)。
/// 无副作用,可直接单元测试。
///
/// 设计要点:
///   - 每种错误类别有确定的"状态转换" 规则(AuthFailure → banned;SpamDetected → cooling 1h 等)
///   - Unknown 不调整状态,但由 SendEngine 根据连续失败阈值做兜底冷却
///   - 兜底规则:连续失败 ≥ CoolingFailures → CoolingMinutes 进入 cooling
/// </summary>
public class SenderFailureClassifierTests
{
    private static Sender MakeSender(string status = "normal", int consecutive = 0, DateTime? cooldownUntil = null)
        => new()
        {
            Id = 1,
            Email = "a@x.com",
            Status = status,
            ConsecutiveFailures = consecutive,
            CooldownUntil = cooldownUntil?.ToString("o"),
        };

    private static AppConfig MakeConfig(int coolingFailures = 3, int coolingMinutes = 60)
        => new() { CoolingFailures = coolingFailures, CoolingMinutes = coolingMinutes };

    private static SmtpSendResult MakeResult(SmtpErrorCategory cat, string? msg = null)
        => new() { Category = cat, ErrorMessage = msg ?? cat.ToString() };

    [Fact]
    public void Classify_AuthFailure_BansAccount()
    {
        var cls = new SenderFailureClassifier();
        var result = cls.Classify(MakeSender(), MakeResult(SmtpErrorCategory.AuthFailure), MakeConfig());

        Assert.Equal(SenderStatus.Banned, result.NewStatus);
        Assert.Null(result.CooldownUntil);   // banned 没有 cooldown,需要人工解封
        Assert.Equal(1, result.ConsecutiveFailures);
    }

    [Fact]
    public void Classify_SpamDetected_CoolsForOneHour()
    {
        var before = DateTime.Now;
        var cls = new SenderFailureClassifier();
        var result = cls.Classify(MakeSender(), MakeResult(SmtpErrorCategory.SpamDetected), MakeConfig());

        Assert.Equal(SenderStatus.Cooling, result.NewStatus);
        Assert.NotNull(result.CooldownUntil);
        // 冷却时长应在 [59min, 61min] 区间(避免时钟漂移误判)
        var span = result.CooldownUntil!.Value - before;
        Assert.InRange(span.TotalMinutes, 59, 61);
    }

    [Fact]
    public void Classify_TemporaryFailure_CoolsFor30Min()
    {
        var before = DateTime.Now;
        var cls = new SenderFailureClassifier();
        var result = cls.Classify(MakeSender(), MakeResult(SmtpErrorCategory.TemporaryFailure), MakeConfig());

        Assert.Equal(SenderStatus.Cooling, result.NewStatus);
        var span = result.CooldownUntil!.Value - before;
        Assert.InRange(span.TotalMinutes, 29, 31);
    }

    [Fact]
    public void Classify_NetworkError_CoolsFor10Min()
    {
        var before = DateTime.Now;
        var cls = new SenderFailureClassifier();
        var result = cls.Classify(MakeSender(), MakeResult(SmtpErrorCategory.NetworkError), MakeConfig());

        Assert.Equal(SenderStatus.Cooling, result.NewStatus);
        var span = result.CooldownUntil!.Value - before;
        Assert.InRange(span.TotalMinutes, 9, 11);
    }

    [Fact]
    public void Classify_UnknownBelowThreshold_NoStatusChange()
    {
        // Unknown 单次不调整状态,但累加 consecutive,留给 SendEngine 阈值兜底
        var cls = new SenderFailureClassifier();
        var result = cls.Classify(MakeSender(consecutive: 1), MakeResult(SmtpErrorCategory.Unknown), MakeConfig());

        Assert.Equal(SenderStatus.Normal, result.NewStatus);
        Assert.Null(result.CooldownUntil);
        Assert.Equal(2, result.ConsecutiveFailures);
    }

    [Fact]
    public void Classify_UnknownAtThreshold_ActivatesCooldown()
    {
        // consecutive 从 2 → 3 = 阈值 → 兜底进入 cooling
        var before = DateTime.Now;
        var cls = new SenderFailureClassifier();
        var result = cls.Classify(MakeSender(consecutive: 2), MakeResult(SmtpErrorCategory.Unknown),
            MakeConfig(coolingFailures: 3, coolingMinutes: 60));

        Assert.Equal(SenderStatus.Cooling, result.NewStatus);
        Assert.NotNull(result.CooldownUntil);
        var span = result.CooldownUntil!.Value - before;
        Assert.InRange(span.TotalMinutes, 59, 61);
        Assert.Equal(3, result.ConsecutiveFailures);
    }

    [Fact]
    public void Classify_Success_ResetsConsecutiveFailures()
    {
        // 成功路径不调 Classify,但如果调了(测试健壮性),应当把状态归 normal、consecutive 清零
        var cls = new SenderFailureClassifier();
        var result = cls.Classify(MakeSender(consecutive: 5), MakeResult(SmtpErrorCategory.Success), MakeConfig());

        Assert.Equal(SenderStatus.Normal, result.NewStatus);
        Assert.Equal(0, result.ConsecutiveFailures);
        Assert.Null(result.CooldownUntil);
    }

    [Fact]
    public void Classify_PreservesCoolingStatus_WhenAlreadyCooling()
    {
        // 已经在 cooling 又来一次 AuthFailure → 应该仍 banned(不会被 cooling 状态干扰)
        var cls = new SenderFailureClassifier();
        var result = cls.Classify(MakeSender(status: "cooling"), MakeResult(SmtpErrorCategory.AuthFailure), MakeConfig());

        Assert.Equal(SenderStatus.Banned, result.NewStatus);
    }
}