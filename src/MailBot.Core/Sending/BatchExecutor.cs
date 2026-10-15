using MailBot.Core.Logging;
using MailBot.Core.Mail;
using MailBot.Core.Models;
using MailBot.Core.Storage;
using MailBot.Core.Verification;
using MimeKit;

namespace MailBot.Core.Sending;

/// <summary>
/// 单批发送的执行体：从 SendEngine.RunAsync 抽出,把"邮箱预校验 → 选 verifier →
/// MailBuilder → SMTP 发送(走 SmtpDispatcher.SendAsync)→ 收件人落库 → 配额计入" 这一长串
/// 串成一次调用。SendEngine 只剩"选账号 / 拉批次 / 调 executor / 失败分类 / 间隔"。
///
/// 设计要点:
///   - 不做账号状态机转换(失败分类交给 SenderFailureClassifier)
///   - 不触发 BatchCompleted 事件(由 SendEngine 在主循环触发)
///   - 不读时间窗口、不睡(纯 I/O 串行动作)
///   - 复用 MailBuilder.BuildForRecipients + VerifierRepository.ListEnabled
/// </summary>
public sealed class BatchExecutor
{
    private readonly SmtpDispatcher _dispatcher;
    private readonly RecipientRepository _recipientRepo;
    private readonly SenderRepository _senderRepo;
    private readonly VerifierRepository _verifierRepo;
    private readonly VerifierConfig _verifierConfig;
    private readonly ILogger? _uiLogger;

    public BatchExecutor(
        SmtpDispatcher dispatcher,
        RecipientRepository recipientRepo,
        SenderRepository senderRepo,
        VerifierRepository verifierRepo,
        VerifierConfig verifierConfig,
        ILogger? uiLogger = null)
    {
        _dispatcher = dispatcher;
        _recipientRepo = recipientRepo;
        _senderRepo = senderRepo;
        _verifierRepo = verifierRepo;
        _verifierConfig = verifierConfig;
        _uiLogger = uiLogger;
    }

    /// <summary>
    /// 执行一次批次：邮箱预校验(无效收件人直接 MarkFailed)→ 选 verifier → 装 MimeMessage →
    /// SMTP 发送 → 收件人落库(unverified / sent)→ 配额计入(无 verifier 路径)。
    /// 返回 SendBatchReport,SmtpResult 字段供 SendEngine 决定失败分类。
    /// </summary>
    public async Task<SendBatchReport> ExecuteAsync(
        Sender sender,
        MailTemplate template,
        List<Recipient> batch,
        CancellationToken ct)
    {
        var report = new SendBatchReport
        {
            StartedAt = DateTime.Now,
            SenderId = sender.Id,
            SenderEmail = sender.Email,
            BatchId = null,           // 调用方(SetBatchId)或后续填充
            BatchSize = batch.Count,
        };

        // 1. 邮箱预校验:把无法解析的地址先标 failed
        var (valid, invalid) = PartitionValid(batch);
        foreach (var bad in invalid)
        {
            _recipientRepo.MarkFailed(bad.Id, "邮箱格式无效");
        }
        report.FailedCount = invalid.Count;
        if (valid.Count == 0)
        {
            // 整批都格式无效:直接返回失败(没真实 SMTP 调用)
            report.SmtpResult = new SmtpSendResult
            {
                Category = SmtpErrorCategory.Unknown,
                ErrorMessage = "批次内无合法收件人",
            };
            report.FinishedAt = DateTime.Now;
            return report;
        }

        // 2. 选 verifier
        var allVerifiers = _verifierRepo.ListEnabled();
        VerifierAccount? firstVerifier = null;
        var verifierBcc = new List<string>();
        if (allVerifiers.Count > 0)
        {
            firstVerifier = allVerifiers[Random.Shared.Next(allVerifiers.Count)];
            verifierBcc.Add(firstVerifier.Email);
        }
        var hasVerifiers = verifierBcc.Count > 0;
        report.VerifierEmail = hasVerifiers ? verifierBcc[0] : null;

        // 3. 装 MimeMessage
        var msg = MailBuilder.BuildForRecipients(sender, template, valid, hasVerifiers ? verifierBcc : null);

        // 4. 一次性 SMTP 发送(NetworkError / Unknown 自动重试一次)
        var result = await SendWithRetryAsync(sender, msg, ct);
        report.SmtpResult = result;
        report.FinishedAt = DateTime.Now;
        if (result.IsSuccess && hasVerifiers) report.SuccessCount = valid.Count;
        else if (result.IsSuccess) report.SuccessCount = valid.Count;

        if (!result.IsSuccess)
        {
            // 失败:什么都不写(收件人保持 pending),由 SendEngine 走分类 + 重试上限
            report.ErrorMessage = result.ErrorMessage ?? result.Category.ToString();
            return report;
        }

        // 5. 成功:落库 + (无验证模式)计入配额
        if (hasVerifiers)
        {
            var batchId = BatchIdGenerator.Next();
            report.BatchId = batchId;
            _recipientRepo.MarkUnverified(valid.Select(r => r.Id), batchId);
            report.FirstCheckAtUtc = _verifierConfig.GetFirstCheckTimeUtc(DateTime.UtcNow);
            _uiLogger?.Emit(LogLevel.Success,
                $"发送 {valid.Count} 封 BCC + 1 封 verifier BCC（…{BatchIdFormatter.Short(batchId)}）");
        }
        else
        {
            // 无验证模式:没有 IMAP 可等,按"默认校验通过"直接落 verified。
            // 这样监控页的送达统计能正常计数,收件人也立刻出队( PickPending 只捞 pending ),
            // 发完一批照常进主循环下一批,不会卡住。
            var today = MailBot.Core.Scheduling.QuotaReset.Today(DateTime.Now);
            _senderRepo.IncrementSentToday(sender.Id, valid.Count, today);
            _recipientRepo.MarkVerifiedWithoutVerification(
                valid.Select(r => r.Id), sender.Id, DateTime.UtcNow);
            _uiLogger?.Emit(LogLevel.Success, $"发送 {valid.Count} 封 BCC（无验证模式，按送达计）");
        }

        // 6. 物理日志:每封收件人一行(SENT 块,便于人工核对)
        WriteSentRecipientBlock(sender.Email, report.BatchId ?? "(no-batch)", valid);
        return report;
    }

    /// <summary>
    /// 给 ResendBatchAsync 复用：仅装 + 发送(无 verifier,直接走单次 SMTP),不写库、不计入配额。
    /// 由 SendEngine.ResendBatchAsync 调用。
    /// </summary>
    public async Task<SmtpSendResult> SendResendAsync(
        Sender sender,
        MailTemplate template,
        List<Recipient> recipients,
        string verifierEmail,
        CancellationToken ct)
    {
        var msg = MailBuilder.BuildForRecipients(sender, template, recipients, verifierBcc: new[] { verifierEmail });
        return await SendWithRetryAsync(sender, msg, ct);
    }

    private async Task<SmtpSendResult> SendWithRetryAsync(Sender sender, MimeMessage msg, CancellationToken ct)
    {
        var first = await _dispatcher.SendAsync(sender, msg, ct);
        if (first.IsSuccess) return first;
        if (first.Category != SmtpErrorCategory.NetworkError
            && first.Category != SmtpErrorCategory.Unknown)
        {
            return first;
        }
        // Network / Unknown 重试一次
        return await _dispatcher.SendAsync(sender, msg, ct);
    }

    private static (List<Recipient> valid, List<Recipient> invalid) PartitionValid(List<Recipient> batch)
    {
        var valid = new List<Recipient>();
        var invalid = new List<Recipient>();
        foreach (var r in batch)
        {
            try
            {
                var addr = MimeKit.MailboxAddress.Parse(r.Email);
                if (!string.IsNullOrWhiteSpace(addr.Address) && addr.Address.Contains('@'))
                {
                    valid.Add(r);
                }
                else
                {
                    invalid.Add(r);
                }
            }
            catch
            {
                invalid.Add(r);
            }
        }
        return (valid, invalid);
    }

    private static void WriteSentRecipientBlock(string senderEmail, string batchId, List<Recipient> valid)
    {
        if (valid.Count == 0) return;
        var sb = new System.Text.StringBuilder(valid.Count * 96);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var r in valid)
        {
            sb.Append('[').Append(stamp).Append("] [SENT] ")
              .Append(r.Email)
              .Append(" | batch=").Append(batchId)
              .Append(" | sender=").Append(senderEmail)
              .Append('\n');
        }
        MailBot.Core.Storage.LogFileWriter.WriteBlock(sb.ToString());
    }
}