using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MailArchiver.Services;

public static partial class OutboundSenderListParser
{
    private const long MaxWorkbookXmlBytes = 25 * 1024 * 1024;
    private const int MaxWorkbookCells = 200_000;
    private const int MaxTextCharacters = 20 * 1024 * 1024;

    [GeneratedRegex(
        @"(?<![A-Z0-9.!#$%&'*+/=?^_`{|}~-])(?<email>[A-Z0-9.!#$%&'*+/=?^_`{|}~-]+@(?:[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\.)+[A-Z]{2,63})(?![A-Z0-9.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex EmailPattern();

    public static IReadOnlySet<string> ExtractFromText(string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sourceText.Length > MaxTextCharacters)
            throw new InvalidDataException("名单内容过大，请缩小后重试。");

        return EmailPattern()
            .Matches(sourceText)
            .Cast<Match>()
            .Select(match => match.Groups["email"].Value.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<IReadOnlySet<string>> ExtractFromFileAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".xlsx" => await ExtractFromXlsxAsync(stream, cancellationToken),
            ".csv" or ".tsv" or ".txt" => await ExtractFromTextFileAsync(stream, cancellationToken),
            _ => throw new InvalidDataException("仅支持 XLSX、CSV、TSV 和 TXT 名单文件。")
        };
    }

    public static byte[] BuildOutboundCsv(IEnumerable<string> senderEmails)
    {
        ArgumentNullException.ThrowIfNull(senderEmails);

        var content = new StringBuilder("\uFEFF时间,发件邮箱,收件邮箱,主题,正文\r\n");
        foreach (var email in senderEmails
                     .Where(email => !string.IsNullOrWhiteSpace(email))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(email => email, StringComparer.OrdinalIgnoreCase))
        {
            content.Append(',')
                .Append(EscapeCsv(email.Trim()))
                .Append(",,,\r\n");
        }

        return Encoding.UTF8.GetBytes(content.ToString());
    }

    private static async Task<IReadOnlySet<string>> ExtractFromTextFileAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return ExtractFromText(text);
    }

    private static async Task<IReadOnlySet<string>> ExtractFromXlsxAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var sharedStrings = await ReadSharedStringsAsync(archive, cancellationToken);
            var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var worksheetBytes = 0L;
            var cellCount = 0;

            foreach (var worksheet in archive.Entries
                         .Where(entry =>
                             entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) &&
                             entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
            {
                worksheetBytes += worksheet.Length;
                if (worksheetBytes > MaxWorkbookXmlBytes)
                    throw new InvalidDataException("XLSX 解压后的工作表内容过大，请缩小后重试。");

                var document = await LoadXmlAsync(worksheet, cancellationToken);
                foreach (var cell in document.Descendants().Where(element => element.Name.LocalName == "c"))
                {
                    cellCount++;
                    if (cellCount > MaxWorkbookCells)
                        throw new InvalidDataException("XLSX 单元格过多，请缩小后重试。");

                    var value = ReadCellValue(cell, sharedStrings);
                    if (!string.IsNullOrWhiteSpace(value))
                        emails.UnionWith(ExtractFromText(value));
                }
            }

            return emails;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException)
        {
            throw new InvalidDataException("无法读取 XLSX 文件，请确认文件没有损坏。", exception);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadSharedStringsAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return [];
        if (entry.Length > MaxWorkbookXmlBytes)
            throw new InvalidDataException("XLSX 共享文本内容过大，请缩小后重试。");

        var document = await LoadXmlAsync(entry, cancellationToken);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "si")
            .Select(ReadInlineString)
            .ToList();
    }

    private static async Task<XDocument> LoadXmlAsync(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        await using var entryStream = entry.Open();
        return await XDocument.LoadAsync(entryStream, LoadOptions.None, cancellationToken);
    }

    private static string ReadCellValue(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "t")?.Value;
        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
            return ReadInlineString(cell);

        var rawValue = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? string.Empty;
        if (!string.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
            return rawValue;

        return int.TryParse(rawValue, out var index) && index >= 0 && index < sharedStrings.Count
            ? sharedStrings[index]
            : string.Empty;
    }

    private static string ReadInlineString(XElement element)
        => string.Concat(element.Descendants()
            .Where(descendant => descendant.Name.LocalName == "t")
            .Select(descendant => descendant.Value));

    private static string EscapeCsv(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
