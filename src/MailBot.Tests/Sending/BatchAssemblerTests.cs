using MailBot.Core.Mail;
using MailBot.Core.Models;
using MailBot.Core.Sending;
using MimeKit;
using Xunit;

namespace MailBot.Tests.Sending;

public class BatchAssemblerTests
{
    [Fact]
    public void Chunk_ExactMultiple()
    {
        var list = Enumerable.Range(1, 10).ToList();
        var chunks = BatchAssembler.Chunk(list, 5).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, chunks[0]);
        Assert.Equal(new[] { 6, 7, 8, 9, 10 }, chunks[1]);
    }

    [Fact]
    public void Chunk_PartialTail()
    {
        var list = Enumerable.Range(1, 12).ToList();
        var chunks = BatchAssembler.Chunk(list, 5).ToList();
        Assert.Equal(3, chunks.Count);
        Assert.Equal(2, chunks[2].Count);
    }

    [Fact]
    public void Chunk_Empty()
    {
        var chunks = BatchAssembler.Chunk(Array.Empty<int>(), 5).ToList();
        Assert.Empty(chunks);
    }
}

public class MailBuilderTests
{
    private static Sender Sender() => new() { Email = "s@x.com" };
    private static MailTemplate Template() => new() { Subject = "t", HtmlBody = "<p>b</p>" };

    [Fact]
    public void Build_SingleRecipient_PutsRecipientInTo()
    {
        // 单收件人：msg.To = 真实收件人，避免"占位 To + 无 Bcc 头"被 QQ 反垃圾标记
        var msg = MailBuilder.Build(Sender(), Template(), new[]
        {
            new Recipient { Email = "user@qq.com" },
        });
        Assert.Single(msg.To);
        Assert.Equal("user@qq.com", ((MailboxAddress)msg.To[0]).Address);
        Assert.Empty(msg.Bcc);
        Assert.Equal("s@x.com", ((MailboxAddress)msg.From[0]).Address);
    }

    [Fact]
    public void Build_MultipleRecipients_FirstInToRestInBcc()
    {
        // 多收件人 BCC：第一个进 To（MailKit 保留在头部），其余进 Bcc（MailKit 自动剥离）
        var msg = MailBuilder.Build(Sender(), Template(), new[]
        {
            new Recipient { Email = "a@x.com" },
            new Recipient { Email = "b@x.com" },
            new Recipient { Email = "c@x.com" },
        });
        Assert.Single(msg.To);
        Assert.Equal("a@x.com", ((MailboxAddress)msg.To[0]).Address);
        Assert.Equal(2, msg.Bcc.Count);
        Assert.Equal("b@x.com", ((MailboxAddress)msg.Bcc[0]).Address);
        Assert.Equal("c@x.com", ((MailboxAddress)msg.Bcc[1]).Address);
    }

    [Fact]
    public void Build_SkipsBlankAndInvalidAddresses()
    {
        var msg = MailBuilder.Build(Sender(), Template(), new[]
        {
            new Recipient { Email = "  " },
            new Recipient { Email = "ok@x.com" },
            new Recipient { Email = "not-an-email" },
        });
        // 只有 ok@x.com 通过解析 → 当作单收件人场景
        Assert.Single(msg.To);
        Assert.Equal("ok@x.com", ((MailboxAddress)msg.To[0]).Address);
        Assert.Empty(msg.Bcc);
    }
}