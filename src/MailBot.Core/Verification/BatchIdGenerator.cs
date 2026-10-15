namespace MailBot.Core.Verification;

/// <summary>
/// 生成批次唯一 ID。格式：YYYY-MM-DD-NNN-XXXX
///   - NNN：当日序号（自进程启动起累加）
///   - XXXX：4 位 GUID 截断（碰撞防护）
/// </summary>
public static class BatchIdGenerator
{
    private static int _counter;
    private static readonly object _lock = new();

    public static string Next()
    {
        var n = NextCounter();
        var suffix = Guid.NewGuid().ToString("N")[..4];
        return $"{DateTime.Now:yyyy-MM-dd}-{n:D3}-{suffix}";
    }

    public static void Reset()
    {
        lock (_lock) { _counter = 0; }
    }

    private static int NextCounter()
    {
        lock (_lock)
        {
            _counter++;
            return _counter;
        }
    }
}