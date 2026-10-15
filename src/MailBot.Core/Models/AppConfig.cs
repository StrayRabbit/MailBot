namespace MailBot.Core.Models;

/// <summary>
/// 运行时应用配置（与 app_config 表对应）。
/// </summary>
public sealed class AppConfig
{
    /// <summary>HH:mm，发送时间窗口起始。</summary>
    public string WindowStart { get; set; } = "09:00";

    /// <summary>HH:mm，发送时间窗口结束。</summary>
    public string WindowEnd { get; set; } = "18:00";

    /// <summary>每批发送间隔秒数。</summary>
    public int SendIntervalSeconds { get; set; } = 30;

    /// <summary>每个 BCC 批次的收件人数。</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>单封失败重试次数。</summary>
    public int MaxRetries { get; set; } = 1;

    /// <summary>
    /// 同一收件人累计失败多少次后被标记为 failed 并停止重试。
    /// 避免结构性错误（账号配置错、收件地址被服务器拒等）下同一批人被无限重复发送。
    /// </summary>
    public int MaxSendAttempts { get; set; } = 3;

    /// <summary>连续失败多少次进入 cooling。</summary>
    public int CoolingFailures { get; set; } = 3;

    /// <summary>cooling 持续分钟数。</summary>
    public int CoolingMinutes { get; set; } = 60;

    /// <summary>从 app_config 表读取的 KV 字典（兜底字段，未识别的键保留在这里）。</summary>
    public Dictionary<string, string> Extra { get; } = new();

    /// <summary>
    /// 校验当前配置的合法性：HH:mm 字符串能解析、整数字段在合理范围内。
    /// 返回所有错误（不抛），调用方聚合展示给用户。空列表 = 通过。
    /// </summary>
    public bool Validate(out IReadOnlyList<string> errors)
    {
        var list = new List<string>();

        // HH:mm 字符串校验
        if (!TryParseHHmm(WindowStart, out _))
        {
            list.Add($"发送窗口起始时间 \"{WindowStart}\" 不是合法的 HH:mm 格式（示例：09:00）");
        }
        if (!TryParseHHmm(WindowEnd, out _))
        {
            list.Add($"发送窗口结束时间 \"{WindowEnd}\" 不是合法的 HH:mm 格式（示例：18:00）");
        }

        // 整数字段范围校验（防御性：UI 校验通过，DB 旧值也可能越界）
        ValidateRange(list, nameof(SendIntervalSeconds), SendIntervalSeconds, min: 1, max: 3600,
            hint: "秒，建议 5~600");
        ValidateRange(list, nameof(BatchSize), BatchSize, min: 1, max: 500,
            hint: "封/批，建议 5~50");
        ValidateRange(list, nameof(MaxRetries), MaxRetries, min: 0, max: 10,
            hint: "次");
        ValidateRange(list, nameof(MaxSendAttempts), MaxSendAttempts, min: 1, max: 20,
            hint: "次");
        ValidateRange(list, nameof(CoolingFailures), CoolingFailures, min: 1, max: 100,
            hint: "次");
        ValidateRange(list, nameof(CoolingMinutes), CoolingMinutes, min: 1, max: 1440,
            hint: "分钟");

        errors = list;
        return list.Count == 0;
    }

    private static void ValidateRange(List<string> errors, string field, int value, int min, int max, string hint)
    {
        if (value < min || value > max)
        {
            errors.Add($"{field} = {value} 超出范围 [{min}, {max}]（{hint}）");
        }
    }

    private static bool TryParseHHmm(string s, out TimeSpan _)
    {
        // HH:mm 严格格式；TimeSpan.TryParse 接受 "9:00" 这种短形式，但调度页只该接受两位小时。
        return TimeSpan.TryParseExact(s, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture,
            out _);
    }
}