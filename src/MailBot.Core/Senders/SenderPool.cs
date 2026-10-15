using MailBot.Core.Models;
using MailBot.Core.Storage;

namespace MailBot.Core.Senders;

/// <summary>
/// 配额检查：判断账号当前是否还有当日额度。
/// </summary>
public sealed class RateLimiter
{
    /// <summary>该账号是否还能继续发件（按 daily_quota / sent_today）。</summary>
    public static bool HasQuota(Sender s) => s.SentToday < s.DailyQuota;

    /// <summary>剩余配额。</summary>
    public static int Remaining(Sender s) => Math.Max(0, s.DailyQuota - s.SentToday);
}

/// <summary>
/// 发件账号选择与状态管理。设计文档 6.4 节定义的选择算法：
///   1) 排除 banned
///   2) 排除 cooling 且 cooldown_until > now
///   3) 排除 sent_today >= daily_quota
///   4) 按剩余配额降序，取首个
/// </summary>
public sealed class SenderPool
{
    private readonly SenderRepository _repo;

    public SenderPool(SenderRepository repo) => _repo = repo;

    /// <summary>挑选下一个可用的账号；返回 null 表示当前没有可用账号。</summary>
    public Sender? PickBestSender(DateTime nowLocal)
    {
        var all = _repo.GetAll();
        return PickBestSender(all, nowLocal);
    }

    /// <summary>纯函数版本：便于测试。</summary>
    public static Sender? PickBestSender(IReadOnlyList<Sender> senders, DateTime nowLocal)
    {
        Sender? best = null;
        var bestRemaining = -1;
        foreach (var s in senders)
        {
            if (s.Status == SenderStatus.Banned) continue;
            if (s.Status == SenderStatus.Cooling
                && !string.IsNullOrEmpty(s.CooldownUntil)
                && DateTime.Parse(s.CooldownUntil) > nowLocal)
                continue;
            if (!RateLimiter.HasQuota(s)) continue;
            var rem = RateLimiter.Remaining(s);
            if (rem > bestRemaining)
            {
                best = s;
                bestRemaining = rem;
            }
        }
        return best;
    }

    /// <summary>
    /// 计算"下一个账号可用"的时刻：取所有不可用账号中最早解锁的时间。
    /// - 冷却中：CooldownUntil
    /// - 配额用尽：次日 00:00
    /// - 封禁：永不解锁，返回 null（表示需要人工介入）
    /// 若所有账号都可用则返回 nowLocal（立即可发）。
    /// </summary>
    public static DateTime? GetEarliestRetryTime(IReadOnlyList<Sender> senders, DateTime nowLocal)
    {
        DateTime? earliest = null;
        foreach (var s in senders)
        {
            if (s.Status == SenderStatus.Banned) continue; // 封禁不参与"自动解锁"
            if (s.Status == SenderStatus.Cooling
                && !string.IsNullOrEmpty(s.CooldownUntil))
            {
                var until = DateTime.Parse(s.CooldownUntil);
                if (until > nowLocal && (earliest is null || until < earliest.Value)) earliest = until;
                continue;
            }
            if (!RateLimiter.HasQuota(s))
            {
                // 配额用尽：最早解锁 = 次日 00:00
                var next = nowLocal.Date.AddDays(1);
                if (earliest is null || next < earliest.Value) earliest = next;
                continue;
            }
        }
        return earliest;
    }

    /// <summary>原子地把账号设为冷却。</summary>
    public void SetCooling(long senderId, TimeSpan duration, int consecutiveFailures)
    {
        var until = DateTime.Now.Add(duration);
        _repo.UpdateStatus(senderId, SenderStatus.Cooling, consecutiveFailures, until);
    }

    /// <summary>账号恢复为 normal 并清零连续失败。</summary>
    public void SetNormal(long senderId)
    {
        _repo.UpdateStatus(senderId, SenderStatus.Normal, consecutiveFailures: 0, cooldownUntil: null);
    }

    /// <summary>账号被封禁（需要人工解除）。</summary>
    public void SetBanned(long senderId, int consecutiveFailures)
    {
        _repo.UpdateStatus(senderId, SenderStatus.Banned, consecutiveFailures, cooldownUntil: null);
    }

    /// <summary>用户手动解除封禁。</summary>
    public void Unban(long senderId) => SetNormal(senderId);
}