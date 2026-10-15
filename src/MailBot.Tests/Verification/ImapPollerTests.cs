using MailBot.Core.Models;
using MailBot.Core.Security;
using MailBot.Core.Verification;
using Xunit;

namespace MailBot.Tests.Verification;

public class ImapPollerTests
{
    private static VerifierAccount FakeAccount() => new()
    {
        Email = "test@qq.com",
        ImapHost = "imap.fake.invalid",
        ImapPort = 993,
        UseSsl = true,
        Username = "test@qq.com",
        // 用合法加密字节，避免反序列化出错——Unprotect 在不可达时会抛，不在 encrypt 路径上
        PasswordEncrypted = CredentialProtector.Protect("fake-password"),
    };

    [Fact]
    public async Task PollSinceAsync_UnreachableHost_ReturnsEmptyAndNoThrow()
    {
        var logs = new List<string>();
        var poller = new ImapPoller(msg => logs.Add(msg));
        var result = await poller.PollSinceAsync(FakeAccount(),
            DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);
        Assert.Empty(result);
        Assert.Single(logs);
        Assert.Contains("imap异常", logs[0]);
        Assert.Contains("test@qq.com", logs[0]);
    }

    [Fact]
    public async Task TestConnectionAsync_UnreachableHost_ReturnsFalse()
    {
        var poller = new ImapPoller(_ => { });
        var (ok, err) = await poller.TestConnectionAsync(FakeAccount(), CancellationToken.None);
        Assert.False(ok);
        Assert.NotNull(err);
    }
}