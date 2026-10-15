namespace MailBot.Core.Scheduling;

/// <summary>
/// 时间窗口判断。设计文档 6.2 节：发送只在窗口期内进行；窗口外引擎睡眠到下次窗口起点。
/// 窗口跨天（如 22:00-06:00）也支持，此时结束时间小于起始时间按"次日"理解。
/// </summary>
public sealed class TimeWindow
{
    public TimeOnly Start { get; }
    public TimeOnly End { get; }

    public TimeWindow(string start, string end)
    {
        Start = TimeOnly.Parse(start);
        End = TimeOnly.Parse(end);
    }

    public TimeWindow(TimeOnly start, TimeOnly end)
    {
        Start = start;
        End = end;
    }

    /// <summary>当前时间是否落在窗口内。</summary>
    public bool IsInWindow(DateTime now)
    {
        var t = TimeOnly.FromDateTime(now);
        if (Start <= End)
        {
            return t >= Start && t < End;
        }
        // 跨天窗口（如 22:00 - 06:00）
        return t >= Start || t < End;
    }

    /// <summary>下一次窗口起点。已在窗口内则返回当前时间。</summary>
    public DateTime NextStart(DateTime now)
    {
        if (IsInWindow(now)) return now;
        var t = TimeOnly.FromDateTime(now);
        DateTime candidate;
        if (Start <= End)
        {
            // 同日窗口：t < Start → 今天 Start；t >= End → 明天 Start
            candidate = t < Start
                ? now.Date.Add(Start.ToTimeSpan())
                : now.Date.AddDays(1).Add(Start.ToTimeSpan());
        }
        else
        {
            // 跨天窗口：t < End → 今天 Start；t >= Start → 明天 Start
            // 但 t 在 [End, Start) 之间表示现在处于"前一天窗口的开始段" → 应返回 now 自身（已在内）
            // 因 IsInWindow 已过滤掉 [Start, End) 之外的情形，这里的 t 一定在 [End, Start)
            candidate = now.Date.AddDays(1).Add(Start.ToTimeSpan());
        }
        return candidate;
    }
}