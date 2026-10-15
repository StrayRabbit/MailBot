using System.Text;
using MailBot.Core.Recipients;
using Xunit;

namespace MailBot.Tests.Recipients;

public class RecipientImporterTests
{
    private static string WriteCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"import_test_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void Import_Csv_WithEmailHeader()
    {
        var path = WriteCsv("email,name\na@x.com,Alice\nb@x.com,Bob\n");
        var result = RecipientImporter.Import(path);
        Assert.Equal(2, result.ImportedRows);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "a@x.com", "b@x.com" },
            result.Recipients.Select(r => r.Email).OrderBy(e => e).ToArray());
        File.Delete(path);
    }

    [Fact]
    public void Import_Csv_NoHeader_FirstColumn()
    {
        var path = WriteCsv("a@x.com\nb@x.com\n");
        var result = RecipientImporter.Import(path);
        Assert.Equal(2, result.ImportedRows);
        Assert.Equal("a@x.com", result.Recipients[0].Email);
        File.Delete(path);
    }

    [Fact]
    public void Import_Csv_InvalidEmail_RecordedAsError()
    {
        var path = WriteCsv("email\ngood@x.com\nnot-an-email\n   \nbad-at-sign@   \n");
        var result = RecipientImporter.Import(path);
        Assert.Equal(1, result.ImportedRows);
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("邮箱为空"));
        Assert.Contains(result.Errors, e => e.Contains("格式异常"));
        File.Delete(path);
    }

    [Fact]
    public void Import_Csv_TrailingSemicolon_IsAccepted()
    {
        // Excel 里每行末尾多敲了一个分号的情况。
        var path = WriteCsv("email\na@x.com;\nb@x.com;\n");
        var result = RecipientImporter.Import(path);
        Assert.Equal(2, result.ImportedRows);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "a@x.com", "b@x.com" }, result.Recipients.Select(r => r.Email).ToArray());
        File.Delete(path);
    }

    [Fact]
    public void Import_Csv_MultipleEmailsInOneCell_AreSplit()
    {
        // 注意要加引号，否则 ASCII 逗号会先被 CsvHelper 当成列分隔符拆成两列，
        // 根本走不到收件人分隔符那一步。
        var path = WriteCsv("email\n\"a@x.com;b@x.com,c@x.com\"\n");
        var result = RecipientImporter.Import(path);
        Assert.Equal(3, result.Recipients.Count);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "a@x.com", "b@x.com", "c@x.com" },
            result.Recipients.Select(r => r.Email).ToArray());
        File.Delete(path);
    }

    [Fact]
    public void Import_Csv_EmailFollowedByNote_KeepsEmailDropsNote()
    {
        // "a@x.com;待跟进" —— 只要能切出合法邮箱，无效碎片静默丢弃，不产生错误行。
        var path = WriteCsv("email\na@x.com;待跟进\n张三 zhangsan@x.com\n");
        var result = RecipientImporter.Import(path);
        Assert.Equal(2, result.Recipients.Count);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "a@x.com", "zhangsan@x.com" },
            result.Recipients.Select(r => r.Email).ToArray());
        File.Delete(path);
    }

    [Fact]
    public void Import_Csv_FullWidthSeparators_AreSplit()
    {
        // 中文 Excel 常见的全角分号/逗号/顿号。
        var path = WriteCsv("email\na@x.com；b@x.com，c@x.com、d@x.com\n");
        var result = RecipientImporter.Import(path);
        Assert.Equal(4, result.Recipients.Count);
        Assert.Empty(result.Errors);
        File.Delete(path);
    }

    [Fact]
    public void Import_Csv_TrailingPunctuation_IsStripped()
    {
        var path = WriteCsv("email\na@x.com。\nb@x.com.\n");
        var result = RecipientImporter.Import(path);
        Assert.Equal(2, result.Recipients.Count);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "a@x.com", "b@x.com" }, result.Recipients.Select(r => r.Email).ToArray());
        File.Delete(path);
    }

    [Fact]
    public void Import_Csv_RowWithNoValidEmail_StillReportedAsError()
    {
        // 切分后一个合法邮箱都切不出来时才报错，保证行号仍可定位。
        var path = WriteCsv("email\na@x.com\n备注;张三;\n");
        var result = RecipientImporter.Import(path);
        Assert.Equal(1, result.Recipients.Count);
        Assert.Single(result.Errors);
        Assert.Contains("第 2 行", result.Errors[0]);
        File.Delete(path);
    }

    [Fact]
    public void Import_Csv_EmailIsLowercased()
    {
        var path = WriteCsv("email\nA@X.COM\n");
        var result = RecipientImporter.Import(path);
        Assert.Equal("a@x.com", result.Recipients[0].Email);
        File.Delete(path);
    }

    [Fact]
    public void Import_UnsupportedExtension_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), "x.txt");
        File.WriteAllText(path, "anything");
        Assert.Throws<NotSupportedException>(() => RecipientImporter.Import(path));
        File.Delete(path);
    }

    [Fact]
    public void Import_Csv_Utf8Bom_DetectsAndParses()
    {
        // 写一个 UTF-8 BOM + 中文 header 的 CSV
        var path = Path.Combine(Path.GetTempPath(), $"import_bom_{Guid.NewGuid():N}.csv");
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var content = "email,姓名\nalice@x.com,爱丽丝\nbob@x.com,鲍勃\n";
        File.WriteAllBytes(path, bom.Concat(Encoding.UTF8.GetBytes(content)).ToArray());
        try
        {
            var result = RecipientImporter.Import(path);
            Assert.Equal(2, result.ImportedRows);
            Assert.Empty(result.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Import_Csv_NonDefaultEncoding_ParsesCorrectly()
    {
        // 用 ISO-8859-1 (Latin-1) 演示"非 UTF-8 默认"编码走通路径 — 模拟用户从
        // 非 UTF-8 平台导出的 CSV 场景。GBK / GB18030 在 Windows 上同样可用
        // （Encoding.GetEncoding("GB18030")），本测试选跨平台稳定的 Latin-1。
        var latin1 = Encoding.GetEncoding("ISO-8859-1");
        var path = Path.Combine(Path.GetTempPath(), $"import_latin1_{Guid.NewGuid():N}.csv");
        // Latin-1 文本：开头 4 字节 0xE9 0xE8 0xE7 0xE6 解码为 é è ç æ
        File.WriteAllBytes(path, latin1.GetBytes("email,note\ncharlie@x.com,éè\ndavid@x.com,çæ\n"));
        try
        {
            var result = RecipientImporter.Import(path, latin1);
            Assert.Equal(2, result.ImportedRows);
            Assert.Empty(result.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }
}