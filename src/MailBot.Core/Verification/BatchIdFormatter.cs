namespace MailBot.Core.Verification;

/// <summary>
/// batchId 展示工具：取最后一个 '-' 之后的 4 字符尾巴（不足 4 字符则取全部），前面加省略号。
/// 与 SendEngine / VerificationWorker 两处 UI 提示前缀保持一致。
/// </summary>
public static class BatchIdFormatter
{
    public static string Short(string batchId)
    {
        if (string.IsNullOrEmpty(batchId)) return "????";
        var idx = batchId.LastIndexOf('-');
        var tail = idx >= 0 ? batchId[(idx + 1)..] : batchId;
        return tail.Length > 4 ? tail[^4..] : tail;
    }
}