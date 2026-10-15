using MailBot.Core.Verification;
using Xunit;

namespace MailBot.Tests.Verification;

public class BatchIdGeneratorTests
{
    public BatchIdGeneratorTests() => BatchIdGenerator.Reset();

    [Fact]
    public void Next_FormatIsCorrect()
    {
        var id = BatchIdGenerator.Next();
        // YYYY-MM-DD-NNN-XXXX
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}-\d{3}-[0-9a-f]{4}$", id);
    }

    [Fact]
    public void Next_TenTimes_AllUnique()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => BatchIdGenerator.Next()).ToList();
        Assert.Equal(10, ids.Distinct().Count());
    }

    [Fact]
    public void Next_SequenceCounterIncrements()
    {
        var a = BatchIdGenerator.Next();
        var b = BatchIdGenerator.Next();
        // 提取 NNN 部分并比较
        var aSeq = int.Parse(a.Split('-')[3]);
        var bSeq = int.Parse(b.Split('-')[3]);
        Assert.Equal(aSeq + 1, bSeq);
    }
}