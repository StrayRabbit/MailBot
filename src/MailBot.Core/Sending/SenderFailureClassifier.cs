using MailBot.Core.Models;
using MailBot.Core.Senders;

namespace MailBot.Core.Sending;

/// <summary>
/// 单次失败分类的结果：决定账号接下来进入什么状态。
/// </summary>
/// <param name="NewStatus">新状态：normal / cooling / banned。</param>
/// <param name="CooldownUntil">冷却到期时间（UTC）；banned 与 normal 时为 null。</param>
/// <param name="ConsecutiveFailures">连续失败计数（成功后会被 SendEngine 清零）。</param>
public sealed record SenderFailureClassification(
    string NewStatus,
    DateTime? CooldownUntil,
    int ConsecutiveFailures);

/// <summary>
/// 从 SendEngine 抽出的纯函数：把 (Sender 当前态 + SMTP 结果) 翻译成 (新状态/冷却/计数)。
///
/// 设计要点：
///   - 每种错误类别有确定的状态转换（AuthFailure → banned；Spam → cooling 1h 等）
///   - Unknown 不主动调整状态，由调用方根据"连续失败 ≥ CoolingFailures"做兜底冷却
///   - 完全无副作用：仅读 sender 字段，不写库、不发事件；落库由 SendEngine 负责
/// </summary>
public sealed class SenderFailureClassifier
{
    /// <summary>分类。Success 也会被处理（normal + 计数清零），方便 SendEngine 在所有路径统一调用。</summary>
    public SenderFailureClassification Classify(Sender sender, SmtpSendResult result, AppConfig config)
    {
        var consecutive = sender.ConsecutiveFailures + 1;

        // 成功：归 normal、清零连续失败
        if (result.IsSuccess)
        {
            return new SenderFailureClassification(
                NewStatus: SenderStatus.Normal,
                CooldownUntil: null,
                ConsecutiveFailures: 0);
        }

        switch (result.Category)
        {
            case SmtpErrorCategory.AuthFailure:
                return new SenderFailureClassification(
                    NewStatus: SenderStatus.Banned,
                    CooldownUntil: null,
                    ConsecutiveFailures: consecutive);

            case SmtpErrorCategory.SpamDetected:
                return new SenderFailureClassification(
                    NewStatus: SenderStatus.Cooling,
                    CooldownUntil: DateTime.Now.AddHours(1),
                    ConsecutiveFailures: consecutive);

            case SmtpErrorCategory.TemporaryFailure:
                return new SenderFailureClassification(
                    NewStatus: SenderStatus.Cooling,
                    CooldownUntil: DateTime.Now.AddMinutes(30),
                    ConsecutiveFailures: consecutive);

            case SmtpErrorCategory.NetworkError:
                return new SenderFailureClassification(
                    NewStatus: SenderStatus.Cooling,
                    CooldownUntil: DateTime.Now.AddMinutes(10),
                    ConsecutiveFailures: consecutive);

            default:
                // Unknown：不主动调整状态；但若 consecutive 已达 CoolingFailures，
                // 自动按 CoolingMinutes 进入冷却（兜底防"Unknown 永远挂账"）。
                var cls = new SenderFailureClassification(
                    NewStatus: sender.Status,
                    CooldownUntil: null,
                    ConsecutiveFailures: consecutive);
                if (consecutive >= config.CoolingFailures)
                {
                    return cls with
                    {
                        NewStatus = SenderStatus.Cooling,
                        CooldownUntil = DateTime.Now.AddMinutes(config.CoolingMinutes),
                    };
                }
                return cls;
        }
    }
}