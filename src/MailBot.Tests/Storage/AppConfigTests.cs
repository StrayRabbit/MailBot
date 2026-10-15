using MailBot.Core.Models;
using Xunit;

namespace MailBot.Tests.Storage;

/// <summary>
/// AppConfig.Validate() 单元测试：覆盖正常值、HH:mm 错误、各整数字段越界。
/// 校验失败时不抛异常，只返回错误列表——这是 SaveConfig 在 UI 拦截非法值前的最后一道防线。
/// </summary>
public class AppConfigTests
{
    private static AppConfig MakeValid() => new()
    {
        WindowStart = "09:00",
        WindowEnd = "18:00",
        SendIntervalSeconds = 30,
        BatchSize = 20,
        MaxRetries = 1,
        MaxSendAttempts = 3,
        CoolingFailures = 3,
        CoolingMinutes = 60,
    };

    [Fact]
    public void Validate_Defaults_ReturnsTrue()
    {
        // 默认值都在合法范围内
        Assert.True(new AppConfig().Validate(out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_CustomValid_ReturnsTrue()
    {
        Assert.True(MakeValid().Validate(out var errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_EqualWindowStartEnd_NoError()
    {
        // WindowStart == WindowEnd 是合法的（语义上 = 全天发送），不报错
        var cfg = MakeValid();
        cfg.WindowStart = "10:00";
        cfg.WindowEnd = "10:00";
        Assert.True(cfg.Validate(out _));
    }

    [Theory]
    [InlineData("9:00")]      // 缺前导 0
    [InlineData("25:00")]     // 小时越界
    [InlineData("09:60")]     // 分钟越界
    [InlineData("abc")]       // 非数字
    [InlineData("")]          // 空串
    [InlineData("09:00:00")]  // 多了秒
    public void Validate_InvalidHHmm_ReturnsError(string input)
    {
        var cfg = MakeValid();
        cfg.WindowStart = input;
        Assert.False(cfg.Validate(out var errors));
        Assert.Contains(errors, e => e.Contains("起始时间"));
    }

    [Fact]
    public void Validate_InvalidHHmm_EndError()
    {
        var cfg = MakeValid();
        cfg.WindowEnd = "abc";
        Assert.False(cfg.Validate(out var errors));
        Assert.Contains(errors, e => e.Contains("结束时间"));
    }

    [Theory]
    [InlineData(0)]      // 必须 ≥1
    [InlineData(-1)]
    [InlineData(3601)]   // 必须 ≤3600
    public void Validate_SendIntervalSeconds_OutOfRange(int v)
    {
        var cfg = MakeValid();
        cfg.SendIntervalSeconds = v;
        Assert.False(cfg.Validate(out var errors));
        Assert.Contains(errors, e => e.Contains(nameof(AppConfig.SendIntervalSeconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void Validate_BatchSize_OutOfRange(int v)
    {
        var cfg = MakeValid();
        cfg.BatchSize = v;
        Assert.False(cfg.Validate(out var errors));
        Assert.Contains(errors, e => e.Contains(nameof(AppConfig.BatchSize)));
    }

    [Fact]
    public void Validate_MultipleErrors_AllReported()
    {
        // 同时多个字段非法,确保错误都被报告(而不是遇到第一个就 return)
        var cfg = new AppConfig
        {
            WindowStart = "abc",
            WindowEnd = "xyz",
            SendIntervalSeconds = -5,
            BatchSize = 9999,
        };
        Assert.False(cfg.Validate(out var errors));
        Assert.True(errors.Count >= 4);
        Assert.Contains(errors, e => e.Contains("起始时间"));
        Assert.Contains(errors, e => e.Contains("结束时间"));
        Assert.Contains(errors, e => e.Contains(nameof(AppConfig.SendIntervalSeconds)));
        Assert.Contains(errors, e => e.Contains(nameof(AppConfig.BatchSize)));
    }

    [Fact]
    public void Validate_BoundaryValues_Accepted()
    {
        // 边界值（min / max）必须被接受
        var cfg = new AppConfig
        {
            WindowStart = "00:00",
            WindowEnd = "23:59",
            SendIntervalSeconds = 1,
            BatchSize = 1,
            MaxRetries = 0,
            MaxSendAttempts = 1,
            CoolingFailures = 1,
            CoolingMinutes = 1,
        };
        Assert.True(cfg.Validate(out var errors), string.Join(";", errors));
    }

    [Fact]
    public void Validate_BoundaryMax_Accepted()
    {
        var cfg = new AppConfig
        {
            WindowStart = "09:00",
            WindowEnd = "18:00",
            SendIntervalSeconds = 3600,
            BatchSize = 500,
            MaxRetries = 10,
            MaxSendAttempts = 20,
            CoolingFailures = 100,
            CoolingMinutes = 1440,
        };
        Assert.True(cfg.Validate(out var errors), string.Join(";", errors));
    }
}