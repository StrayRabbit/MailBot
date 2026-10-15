using System.Globalization;
using System.Text;
using CsvHelper;
using MailBot.Core.Models;
using OfficeOpenXml;

namespace MailBot.Core.Recipients;

/// <summary>
/// 把收件人列表与状态导出回 Excel/CSV。设计文档 4.1 节。
/// </summary>
public static class RecipientExporter
{
    public static void Export(IEnumerable<Recipient> recipients, string outputPath)
    {
        var ext = Path.GetExtension(outputPath).ToLowerInvariant();
        var list = recipients.ToList();
        switch (ext)
        {
            case ".xlsx": ExportExcel(list, outputPath); break;
            case ".csv": ExportCsv(list, outputPath); break;
            default: throw new NotSupportedException($"不支持的文件类型：{ext}");
        }
    }

    private static void ExportCsv(List<Recipient> list, string path)
    {
        // 用 UTF-8 with BOM 输出，Excel 在中文 Windows 上能正确识别并显示。
        // 不带 BOM 时 Excel 默认按 GBK 解码，中文乱码。
        using var writer = new StreamWriter(path, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteField("email");
        csv.WriteField("status");
        csv.WriteField("sent_at");
        csv.WriteField("error_message");
        csv.WriteField("attempt_count");
        csv.WriteField("last_attempt_at");
        csv.WriteField("batch_id");
        csv.WriteField("verified_at");
        csv.WriteField("source_file");
        csv.NextRecord();
        foreach (var r in list)
        {
            csv.WriteField(r.Email);
            csv.WriteField(r.Status);
            csv.WriteField(r.SentAt?.ToString("o") ?? string.Empty);
            csv.WriteField(r.ErrorMessage ?? string.Empty);
            csv.WriteField(r.AttemptCount);
            csv.WriteField(r.LastAttemptAt?.ToString("o") ?? string.Empty);
            csv.WriteField(r.BatchId ?? string.Empty);
            csv.WriteField(r.VerifiedAt?.ToString("o") ?? string.Empty);
            csv.WriteField(r.SourceFile ?? string.Empty);
            csv.NextRecord();
        }
    }

    private static void ExportExcel(List<Recipient> list, string path)
    {
        ExcelPackage.License.SetNonCommercialPersonal("MailBot");
        using var pkg = new ExcelPackage();
        var sheet = pkg.Workbook.Worksheets.Add("Recipients");
        sheet.Cells[1, 1].Value = "email";
        sheet.Cells[1, 2].Value = "status";
        sheet.Cells[1, 3].Value = "sent_at";
        sheet.Cells[1, 4].Value = "error_message";
        sheet.Cells[1, 5].Value = "attempt_count";
        sheet.Cells[1, 6].Value = "last_attempt_at";
        sheet.Cells[1, 7].Value = "batch_id";
        sheet.Cells[1, 8].Value = "verified_at";
        sheet.Cells[1, 9].Value = "source_file";
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            int row = i + 2;
            sheet.Cells[row, 1].Value = r.Email;
            sheet.Cells[row, 2].Value = r.Status;
            sheet.Cells[row, 3].Value = r.SentAt?.ToString("o") ?? string.Empty;
            sheet.Cells[row, 4].Value = r.ErrorMessage ?? string.Empty;
            sheet.Cells[row, 5].Value = r.AttemptCount;
            sheet.Cells[row, 6].Value = r.LastAttemptAt?.ToString("o") ?? string.Empty;
            sheet.Cells[row, 7].Value = r.BatchId ?? string.Empty;
            sheet.Cells[row, 8].Value = r.VerifiedAt?.ToString("o") ?? string.Empty;
            sheet.Cells[row, 9].Value = r.SourceFile ?? string.Empty;
        }
        pkg.SaveAs(new FileInfo(path));
    }
}