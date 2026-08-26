using System.IO.Compression;
using System.Text;
using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class OutboundSenderListParserTests
{
    [Fact]
    public void ExtractFromText_finds_unique_emails_in_pasted_table_content()
    {
        var result = OutboundSenderListParser.ExtractFromText(
            "姓名\t账号\t备注\r\n张三\tAlice@Example.com\t主账号\r\n" +
            "李四,bob.sales+1@sub.example.org,重复 alice@example.com");

        Assert.Equal(2, result.Count);
        Assert.Contains("alice@example.com", result);
        Assert.Contains("bob.sales+1@sub.example.org", result);
    }

    [Fact]
    public async Task ExtractFromFile_reads_shared_and_inline_strings_from_xlsx()
    {
        await using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(
                archive,
                "xl/sharedStrings.xml",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <si><t>shared@example.com</t></si>
                  <si><r><t>prefix </t></r><r><t>rich@example.net</t></r></si>
                </sst>
                """);
            await WriteEntryAsync(
                archive,
                "xl/worksheets/sheet1.xml",
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData><row>
                    <c r="A1" t="s"><v>0</v></c>
                    <c r="B1" t="s"><v>1</v></c>
                    <c r="C1" t="inlineStr"><is><t>inline@example.org</t></is></c>
                    <c r="D1"><v>12345</v></c>
                  </row></sheetData>
                </worksheet>
                """);
        }
        stream.Position = 0;

        var result = await OutboundSenderListParser.ExtractFromFileAsync(
            stream,
            "名单.xlsx",
            CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Contains("shared@example.com", result);
        Assert.Contains("rich@example.net", result);
        Assert.Contains("inline@example.org", result);
    }

    [Fact]
    public void BuildOutboundCsv_writes_one_sorted_row_per_unique_sender()
    {
        var content = Encoding.UTF8.GetString(OutboundSenderListParser.BuildOutboundCsv(
            ["b@example.com", "A@example.com", "a@example.com"]));

        Assert.Equal(
            "\uFEFF时间,发件邮箱,收件邮箱,主题,正文\r\n" +
            ",A@example.com,,,\r\n" +
            ",b@example.com,,,\r\n",
            content);
    }

    [Fact]
    public async Task ExtractFromFile_rejects_unsupported_formats()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("user@example.com"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            OutboundSenderListParser.ExtractFromFileAsync(stream, "名单.xls", CancellationToken.None));

        Assert.Contains("XLSX", exception.Message);
    }

    [Fact]
    public async Task ExtractFromFile_reports_a_clear_error_for_malformed_xlsx_xml()
    {
        await using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteEntryAsync(
                archive,
                "xl/worksheets/sheet1.xml",
                "<worksheet><broken></worksheet>");
        }
        stream.Position = 0;

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            OutboundSenderListParser.ExtractFromFileAsync(stream, "损坏名单.xlsx", CancellationToken.None));

        Assert.Contains("文件没有损坏", exception.Message);
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content);
    }
}
