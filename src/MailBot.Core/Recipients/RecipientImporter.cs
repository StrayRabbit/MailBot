using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using MailBot.Core.Models;
using OfficeOpenXml;

namespace MailBot.Core.Recipients;

/// <summary>
/// 从 Excel/CSV 导入收件人列表。设计文档 4.1 + 9 节：
///   - CSV 用 CsvHelper 流式读取，避免 1 万行时内存峰值
///   - Excel 用 EPPlus 一次性读
/// 列识别：优先表头 "email"，否则取第一列。
/// </summary>
public static class RecipientImporter
{
    public sealed class ImportResult
    {
        public List<Recipient> Recipients { get; } = new();
        public List<string> Errors { get; } = new();
        public int TotalRows { get; set; }
        public int ImportedRows { get; set; }
    }

    public static ImportResult Import(string filePath)
        => Import(filePath, DetectEncoding(filePath));

    /// <summary>
    /// 支持指定编码（解决 Windows GBK/GB2312 CSV 导入乱码）。
    /// 典型用法：UI 提供"编码"下拉让用户手动选 GBK / UTF-8 / UTF-16。
    /// </summary>
    public static ImportResult Import(string filePath, Encoding encoding)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" => ImportExcel(filePath),
            ".csv" => ImportCsv(filePath, encoding),
            _ => throw new NotSupportedException($"不支持的文件类型：{ext}"),
        };
    }

    /// <summary>
    /// 通过文件首三字节识别编码：
    ///   - EF BB BF → UTF-8 with BOM
    ///   - FF FE    → UTF-16 LE
    ///   - FE FF    → UTF-16 BE
    ///   - 否则按 UTF-8（无 BOM 也是合法 UTF-8，GBK 文件会乱码，UI 应提供手动覆盖）
    /// </summary>
    private static Encoding DetectEncoding(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> head = stackalloc byte[3];
            int read = fs.Read(head);
            if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE)
                return Encoding.Unicode;
            if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF)
                return Encoding.BigEndianUnicode;
        }
        catch
        {
            // 探测失败按 UTF-8 fallback
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static ImportResult ImportCsv(string filePath)
        => ImportCsv(filePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static ImportResult ImportCsv(string filePath, Encoding encoding)
    {
        var result = new ImportResult();
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        };

        using var reader = new StreamReader(filePath, encoding);
        using var csv = new CsvReader(reader, config);

        // 先把第一行读进来，自己判断它是不是真表头：包含 "@" 的视为数据行（无表头 CSV）。
        if (!csv.Read()) return result;
        var firstRow = csv.Parser.Record ?? Array.Empty<string>();
        bool firstIsHeader = !firstRow.Any(f => f.Contains('@'));
        int firstDataRowIndex;
        if (firstIsHeader)
        {
            csv.ReadHeader();
            firstDataRowIndex = -1; // 下一次 Read() 即开始数据
        }
        else
        {
            // 没有表头：把第一行当作数据
            firstDataRowIndex = 0;
        }

        int emailCol = firstIsHeader
            ? ResolveEmailColumn(csv.HeaderRecord)
            : 1; // 默认第一列

        // 处理"被识别为表头"的第一行
        if (firstDataRowIndex == 0)
        {
            result.TotalRows++;
            var email = SafeGetByIndex(firstRow, emailCol - 1);
            if (TryAddEmail(result, email, filePath, result.TotalRows))
            {
                result.ImportedRows++;
            }
        }

        while (csv.Read())
        {
            result.TotalRows++;
            var email = SafeGet(csv, emailCol - 1);
            if (TryAddEmail(result, email, filePath, result.TotalRows))
            {
                result.ImportedRows++;
            }
        }
        return result;
    }

    private static bool TryAddEmail(ImportResult result, string? raw, string filePath, int rowNumber)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            result.Errors.Add($"第 {rowNumber} 行：邮箱为空");
            return false;
        }

        // 一格里可能有多个邮箱（用分隔符隔开），也可能夹杂姓名/备注等无关文字。
        // 规则：整行只要能切出至少一个合法邮箱就全部收下，无效碎片静默丢弃；
        // 一个都切不出来才报错，帮用户定位到具体行号。
        var parts = SplitEmails(raw);
        var accepted = 0;
        string? firstInvalid = null;
        foreach (var part in parts)
        {
            if (!LooksLikeEmail(part))
            {
                firstInvalid ??= part;
                continue;
            }
            result.Recipients.Add(new Recipient
            {
                Email = part.ToLowerInvariant(),
                ImportedAt = DateTime.UtcNow,
                SourceFile = Path.GetFileName(filePath),
            });
            accepted++;
        }

        if (accepted > 0) return true;

        if (parts.Count == 0)
        {
            result.Errors.Add($"第 {rowNumber} 行：邮箱为空");
        }
        else
        {
            result.Errors.Add($"第 {rowNumber} 行：邮箱格式异常 ({firstInvalid})");
        }
        return false;
    }

    /// <summary>
    /// 按常见分隔符把一个单元格切成候选邮箱片段，并去掉首尾空白与句读。
    /// 半角/全角分号、逗号、顿号、空格、制表符、换行都算分隔符——
    /// 邮箱本身不含这些字符，<see cref="LooksLikeEmail"/> 也已显式拒绝空格。
    /// </summary>
    private static List<string> SplitEmails(string raw)
    {
        var parts = new List<string>();
        foreach (var token in raw.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim(TrimChars);
            if (trimmed.Length > 0) parts.Add(trimmed);
        }
        return parts;
    }

    private static readonly char[] SplitChars =
        { ';', '；', ',', '，', '、', ' ', '\t', '\r', '\n' };

    /// <summary>片段首尾要剥掉的标点，如中文句尾的“。”或 CSV 常见的尾随逗号。</summary>
    private static readonly char[] TrimChars =
        { '。', '．', '.', '、', ',', '，', ';', '；', '!', '！', '?', '？' };


    private static string SafeGetByIndex(string[] row, int idx)
    {
        return idx >= 0 && idx < row.Length ? row[idx] : string.Empty;
    }

    private static ImportResult ImportExcel(string filePath)
    {
        var result = new ImportResult();
        ExcelPackage.License.SetNonCommercialPersonal("MailBot");
        using var pkg = new ExcelPackage(new FileInfo(filePath));
        var sheet = pkg.Workbook.Worksheets[0];
        if (sheet is null || sheet.Dimension is null) return result;

        int emailCol = 1;
        // 第 1 行作为表头
        var header = sheet.Cells[1, 1, 1, sheet.Dimension.End.Column];
        var headerTexts = new List<string?>();
        for (int c = 1; c <= sheet.Dimension.End.Column; c++)
        {
            headerTexts.Add(sheet.Cells[1, c].Value?.ToString());
        }
        emailCol = ResolveEmailColumn(headerTexts.ToArray());
        int lastRow = sheet.Dimension.End.Row;

        for (int r = 2; r <= lastRow; r++)
        {
            result.TotalRows++;
            var raw = sheet.Cells[r, emailCol].Value?.ToString();
            if (TryAddEmail(result, raw, filePath, r))
            {
                result.ImportedRows++;
            }
        }
        return result;
    }

    private static int ResolveEmailColumn(string?[]? headers)
    {
        if (headers is null) return 1;
        for (int i = 0; i < headers.Length; i++)
        {
            if (string.Equals(headers[i]?.Trim(), "email", StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return 1;
    }

    private static string SafeGet(CsvReader csv, int col)
    {
        try { return csv.GetField(col) ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>邮箱格式的轻量校验。空格/Tab 会被 <see cref="SplitEmails"/> 当分隔符提前切掉，这里不再重复拦截。</summary>
    public static bool LooksLikeEmail(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var at = s.IndexOf('@');
        if (at <= 0 || at == s.Length - 1) return false;
        return !s.Contains(' ', StringComparison.Ordinal)
            && !s.Contains('\t', StringComparison.Ordinal)
            && s.IndexOf('@', at + 1) < 0;
    }
}