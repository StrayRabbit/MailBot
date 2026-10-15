namespace MailBot.Core.Models;

/// <summary>
/// 某天（本地展示日，按 <see cref="MailBot.Core.DisplayTimeZone.ToDisplay(DateTime)"/> 换算）
/// 成功发出的不同 batch_id 数量。监控页柱状图按这个模型渲染。
/// </summary>
public readonly record struct DailyBatchCount(DateOnly Day, int BatchCount);

/// <summary>
/// 某天（本地展示日，按 <see cref="MailBot.Core.DisplayTimeZone.ToDisplay(DateTime)"/> 换算）
/// 验证通过的收件人数（recipients.status='verified' 且 verified_at 落在该日）。
/// 统计页柱状图按这个模型渲染。
/// </summary>
public readonly record struct DailySendCount(DateOnly Day, int VerifiedCount);