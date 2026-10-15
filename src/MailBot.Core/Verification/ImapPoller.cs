using MailBot.Core.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace MailBot.Core.Verification;

/// <summary>
/// 基于 MailKit 的 IMAP 拉取实现。**所有异常内部吞掉**，返回空集合 + 记录到回调日志。
/// 重要规则：
///   1) 不删除邮件（保留原文便于肉眼核对）
///   2) 按时间窗查询（receivedSince 之后的所有邮件），不依赖水位线
///   3) 任意异常 → 不抛出 → 不影响 worker 其他邮箱
/// </summary>
public sealed class ImapPoller : IImapPoller
{
    private readonly Action<string> _log;

    public ImapPoller(Action<string> log) => _log = log;

    public async Task<IReadOnlyList<(string FromEmail, uint Uid, DateTimeOffset? InternalDate)>>
        PollSinceAsync(
            VerifierAccount account, DateTimeOffset receivedSince, CancellationToken ct)
    {
        try
        {
            using var client = new ImapClient();
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            await client.ConnectAsync(account.ImapHost, account.ImapPort,
                account.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);

            var pwd = MailBot.Core.Security.CredentialProtector.Unprotect(account.PasswordEncrypted);
            await client.AuthenticateAsync(account.Username, pwd, ct);

            var inbox = client.Inbox;
            await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly, ct);

            // IMAP SEARCH SINCE 是 day-precision：只到日期，不到时分秒，按"Received"日期过滤。
            // MailKit 用 SearchQuery.DeliveredAfter(date) — IMAP 服务端按它自己的时区比较 Received date，
            // 只看 DD-Mon-YYYY 这一天的起点，丢弃时间部分。
            // 这里传 UTC，MailKit 内部按 IMAP 协议编码。**调用方**必须在客户端二次过滤
            // INTERNALDATE >= sinceUtc，避免同一 GMT+8 日内多批 BCC 让老批次邮件误命中当前批次。
            var sinceUtc = receivedSince.UtcDateTime;
            var uids = await inbox.SearchAsync(SearchQuery.DeliveredAfter(sinceUtc), ct);

            var results = new List<(string, uint, DateTimeOffset?)>();
            if (uids.Count > 0)
            {
                // 合并发送后 verifier 匹配键改为「verifier 收到 sender.Email 的邮件」，
                // 不再读主题（[batch=xxx] 前缀机制已删除）。
                // MailKit 的 MessageSummaryItems 没有 From 字段，要拉 Envelope 才能取 From；
                // Envelope.From 是 InternetAddressList，里面是 InternetAddress（基类），强转为 MailboxAddress 取 Address。
                var items = await inbox.FetchAsync(uids,
                    MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate,
                    ct);
                foreach (var item in items)
                {
                    var fromMailbox = item.Envelope?.From?.OfType<MimeKit.MailboxAddress>().FirstOrDefault();
                    var fromEmail = fromMailbox?.Address ?? string.Empty;
                    results.Add((
                        fromEmail,
                        item.UniqueId.Id,
                        item.InternalDate));
                }
            }
            await client.DisconnectAsync(quit: true, ct);
            return results;
        }
        catch (Exception ex)
        {
            _log($"[imap异常] {account.Email}: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<(string, uint, DateTimeOffset?)>();
        }
    }

    public async Task<(bool ok, string? error)> TestConnectionAsync(
        VerifierAccount account, CancellationToken ct)
    {
        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync(account.ImapHost, account.ImapPort,
                account.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
            var pwd = MailBot.Core.Security.CredentialProtector.Unprotect(account.PasswordEncrypted);
            await client.AuthenticateAsync(account.Username, pwd, ct);
            await client.DisconnectAsync(quit: true, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<uint?> GetInboxMaxUidAsync(
        VerifierAccount account, CancellationToken ct)
    {
        try
        {
            using var client = new ImapClient();
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            await client.ConnectAsync(account.ImapHost, account.ImapPort,
                account.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
            var pwd = MailBot.Core.Security.CredentialProtector.Unprotect(account.PasswordEncrypted);
            await client.AuthenticateAsync(account.Username, pwd, ct);
            var inbox = client.Inbox;
            await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly, ct);
            // 取收件箱最大 UID：UIDNext - 1（IMAP UIDNext 是下一封将分配的 UID，最大已分配 = UIDNext-1）。
            uint? max = null;
            try
            {
                var uidNext = inbox.UidNext;
                if (uidNext.HasValue && uidNext.Value.Id > 1) max = uidNext.Value.Id - 1;
            }
            catch { /* 某些服务端不暴露 UidNext，忽略 */ }
            await client.DisconnectAsync(quit: true, ct);
            return max;
        }
        catch (Exception ex)
        {
            _log($"[imap异常] {account.Email} GetInboxMaxUidAsync: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}