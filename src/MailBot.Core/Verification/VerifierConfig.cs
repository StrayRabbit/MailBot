using MailBot.Core.Storage;

namespace MailBot.Core.Verification;

/// <summary>
/// 自动送达验证的强类型配置。从 app_config 表加载，提供默认值与合法性校验。
/// </summary>
public sealed class VerifierConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 两次轮询之间的间隔（秒）。同时也是"首查前等待时长"的默认值。
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 180;

    /// <summary>
    /// 批次发送后，等待多久才进入"首查"。默认等于 PollIntervalSeconds。
    /// </summary>
    public int RecheckIntervalSeconds { get; set; } = 180;

    /// <summary>
    /// 验证邮箱"轮询次数"上限：批次检查达到此次数后仍未命中 → MarkUnconfirmed
    /// （同时回退 pending + 账号冷却）。默认 2 表示首查 + 重查两次机会。
    /// </summary>
    public int MaxPollAttempts { get; set; } = 2;

    /// <summary>
    /// IMAP 查询时间窗（小时）：每次 PollOnce 只查"过去此值内"的邮件。
    /// 不再使用 UID 水位线，按 Received 时间窗扫描；重复扫描由批次状态机去重
    /// （已 Verified / Unconfirmed 的批次不再参与判定）。
    /// 默认 1 小时——通常一轮发送→送达验证→IMAP 接收的时间远小于 1h。
    /// </summary>
    public int LookbackHours { get; set; } = 1;

    /// <summary>
    /// 批次最大存活时长（分钟）：批次 SentAt 之后超过此分钟数即使还在轮询也强制 Unconfirmed。
    /// 默认 5 分钟——典型 IMAP 服务端延迟 &lt; 1min，5min 足够覆盖；过大则账号恢复慢。
    /// 与 RecheckIntervalSeconds + MaxPollAttempts 的"轮询节奏"独立：实际轮询节奏可能比 LookbackMinutes 更长
    /// （如 RecheckIntervalSeconds=180s × MaxPollAttempts=2 = 360s = 6 分钟），这时 Unconfirmed 触发时机
    /// 由轮询节奏决定，LookbackMinutes 只是兜底（仅在 worker 出现异常长时间未轮询时生效）。
    /// </summary>
    public int LookbackMinutes { get; set; } = 5;

    /// <summary>
    /// 验证邮箱达到 MaxPollAttempts 仍未命中后，对发件账号触发的冷却时长（分钟）。
    /// 同时该批次收件人会被回退为 pending，冷却到期后随主循环重新发送。
    /// </summary>
    public int UnconfirmedCooldownMinutes { get; set; } = 60;

    /// <summary>
    /// 每个批次最多试几个 verifier；达到后即使还有未试的 verifier 也算 unconfirmed。
    /// 默认 2：给一次扩选机会。
    /// </summary>
    public int MaxVerifierAttemptsPerBatch { get; set; } = 2;

    /// <summary>
    /// 翻倍冷却的上限（分钟）。默认 1440 (24h)：防止冷却期无限增长。
    /// </summary>
    public int CooldownDoublingCapMinutes { get; set; } = 1440;

    /// <summary>
    /// SendEngine 在发完一批后、最多重试等待该秒数后跳过阻塞。
    /// 保留以兼容旧 app_config 数据；新逻辑不再需要。
    /// </summary>
    public int SendHoldOnUnverifiedSeconds { get; set; } = 300;

    // 历史保留字段：旧"乐观通过"机制使用。新逻辑已不再读取，仅保留属性以兼容旧 app_config 数据。
    public int ConfirmTimeoutSeconds { get; set; } = 180;
    public int UnconfirmTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// 从 ConfigRepository 加载配置（缺省值兜底）。
    /// 注意：本方法不调用 Validate()，由调用方决定何时校验（启动时、修改配置后）。
    /// </summary>
    public static VerifierConfig Load(ConfigRepository repo)
    {
        var cfg = new VerifierConfig
        {
            Enabled = ParseInt(repo.Get("verification_enabled"), 1) != 0,
            PollIntervalSeconds = ParseInt(repo.Get("verification_poll_interval_seconds"), 180),
            ConfirmTimeoutSeconds = ParseInt(repo.Get("verification_confirm_timeout_seconds"), 180),
            UnconfirmTimeoutSeconds = ParseInt(repo.Get("verification_unconfirm_timeout_seconds"), 600),
            SendHoldOnUnverifiedSeconds = ParseInt(repo.Get("send_hold_on_unverified_seconds"), 300),
            RecheckIntervalSeconds = ParseInt(repo.Get("verification_recheck_interval_seconds"), 180),
            MaxPollAttempts = ParseInt(repo.Get("verification_max_poll_attempts"), 2),
            LookbackHours = ParseInt(repo.Get("verification_lookback_hours"), 1),
            LookbackMinutes = ParseInt(repo.Get("verification_lookback_minutes"), 5),
            UnconfirmedCooldownMinutes = ParseInt(repo.Get("verification_unconfirmed_cooldown_minutes"), 60),
            MaxVerifierAttemptsPerBatch = ParseInt(repo.Get("verification_max_verifier_attempts_per_batch"), 2),
            CooldownDoublingCapMinutes = ParseInt(repo.Get("verification_cooldown_doubling_cap_minutes"), 1440),
        };
        return cfg;
    }

    /// <summary>
    /// 校验配置合法性：MaxPollAttempts ≥ 1；LookbackHours ≥ 0；UnconfirmedCooldownMinutes ≥ 0。
    /// </summary>
    public void Validate()
    {
        if (MaxPollAttempts < 1)
        {
            throw new InvalidOperationException(
                $"verification_max_poll_attempts ({MaxPollAttempts}) 必须 ≥ 1，否则批次永远无法被判定 Unconfirmed。");
        }
        if (LookbackHours < 0)
        {
            throw new InvalidOperationException(
                $"verification_lookback_hours ({LookbackHours}) 必须 ≥ 0。");
        }
        if (LookbackMinutes < 1)
        {
            throw new InvalidOperationException(
                $"verification_lookback_minutes ({LookbackMinutes}) 必须 ≥ 1，否则批次永远无法被强制 Unconfirmed。");
        }
        if (LookbackMinutes * 60 < RecheckIntervalSeconds)
        {
            throw new InvalidOperationException(
                $"verification_lookback_minutes ({LookbackMinutes}) * 60 必须 ≥ RecheckIntervalSeconds ({RecheckIntervalSeconds})，否则首查都没机会查就 Unconfirmed 了。");
        }
        if (UnconfirmedCooldownMinutes < 0)
        {
            throw new InvalidOperationException(
                $"verification_unconfirmed_cooldown_minutes ({UnconfirmedCooldownMinutes}) 必须 ≥ 0。");
        }
        if (MaxVerifierAttemptsPerBatch < 1)
        {
            throw new InvalidOperationException(
                $"verification_max_verifier_attempts_per_batch ({MaxVerifierAttemptsPerBatch}) 必须 ≥ 1");
        }
        if (CooldownDoublingCapMinutes < UnconfirmedCooldownMinutes)
        {
            throw new InvalidOperationException(
                $"verification_cooldown_doubling_cap_minutes ({CooldownDoublingCapMinutes}) 必须 ≥ " +
                $"UnconfirmedCooldownMinutes ({UnconfirmedCooldownMinutes})，否则永远会立即封顶");
        }
    }

    private static int ParseInt(string? s, int dflt)
        => int.TryParse(s, out var v) ? v : dflt;

    /// <summary>
    /// 翻倍公式：attempts=1→BaseCooldown, attempts=2→2*BaseCooldown, ...
    /// 任何超出 Cap 的值截到 Cap。attempts ≤ 0 兜底返回 BaseCooldown。
    /// </summary>
    internal int ComputeCooldownMinutes(int attempts)
    {
        if (attempts <= 0) return UnconfirmedCooldownMinutes;
        var multiplier = 1L << Math.Min(attempts - 1, 20); // 防溢出（>20 后封顶）
        var raw = (long)UnconfirmedCooldownMinutes * multiplier;
        return (int)Math.Min(raw, CooldownDoublingCapMinutes);
    }

    /// <summary>
    /// 计算 worker 首次拉取 IMAP 的 UTC 时刻 = sentAtUtc + RecheckIntervalSeconds。
    /// UI 直接拿这个值显示"首查 HH:mm:ss"，不用自己知道 RecheckIntervalSeconds。
    /// </summary>
    public DateTime GetFirstCheckTimeUtc(DateTime sentAtUtc)
        => sentAtUtc.AddSeconds(RecheckIntervalSeconds);
}