using System.Globalization;
using System.Net.Mail;
using Microsoft.VisualBasic.FileIO;

namespace MailArchiver.Services;

public sealed record OutboundMailTaskCsvRow(
    int LineNumber,
    DateTime ScheduledAtUtc,
    string Sender,
    string Recipient,
    string Subject,
    string Body);

public sealed record OutboundMailTaskCsvError(int LineNumber, string Reason);

public sealed record OutboundMailTaskCsvParseResult(
    IReadOnlyList<OutboundMailTaskCsvRow> Rows,
    IReadOnlyList<OutboundMailTaskCsvError> Errors);

public static class OutboundMailTaskCsvParser
{
    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy/M/d H:mm:ss",
        "yyyy/M/d H:mm"
    ];

    private static readonly IReadOnlyDictionary<string, string> HeaderAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["时间"] = "time",
            ["发送时间"] = "time",
            ["定时时间"] = "time",
            ["time"] = "time",
            ["scheduledat"] = "time",
            ["发件邮箱"] = "sender",
            ["发件人"] = "sender",
            ["sender"] = "sender",
            ["from"] = "sender",
            ["收件邮箱"] = "recipient",
            ["收件人"] = "recipient",
            ["recipient"] = "recipient",
            ["to"] = "recipient",
            ["主题"] = "subject",
            ["subject"] = "subject",
            ["正文"] = "body",
            ["body"] = "body"
        };

    public static OutboundMailTaskCsvParseResult Parse(
        TextReader reader,
        DateTime utcNow,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(localTimeZone);

        var rows = new List<OutboundMailTaskCsvRow>();
        var errors = new List<OutboundMailTaskCsvError>();

        using var parser = new TextFieldParser(reader)
        {
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");

        string[]? headers;
        try
        {
            headers = parser.ReadFields();
        }
        catch (MalformedLineException)
        {
            return new([], [new OutboundMailTaskCsvError(1, "CSV 表头格式不正确。")] );
        }

        if (headers is null || headers.Length == 0)
            return new([], [new OutboundMailTaskCsvError(1, "CSV 不能为空。")] );

        var index = CreateHeaderIndex(headers, errors);
        if (errors.Count > 0)
            return new(rows, errors);

        while (!parser.EndOfData)
        {
            var lineNumber = checked((int)parser.LineNumber);
            string[]? fields;
            try
            {
                fields = parser.ReadFields();
            }
            catch (MalformedLineException)
            {
                errors.Add(new OutboundMailTaskCsvError(lineNumber, "CSV 行格式不正确，请检查引号和逗号。"));
                continue;
            }

            if (fields is null || fields.All(string.IsNullOrWhiteSpace))
                continue;

            if (fields.Length != headers.Length)
            {
                errors.Add(new OutboundMailTaskCsvError(lineNumber, $"应有 {headers.Length} 列，实际为 {fields.Length} 列。"));
                continue;
            }

            var sender = fields[index["sender"]].Trim();
            var recipient = fields[index["recipient"]].Trim();
            var subject = fields[index["subject"]].Trim();
            var body = fields[index["body"]];
            var time = fields[index["time"]].Trim();
            var rowErrors = new List<string>();

            if (!IsValidEmail(sender))
                rowErrors.Add("发件邮箱格式不正确");
            if (!IsValidEmail(recipient))
                rowErrors.Add("收件邮箱格式不正确");
            if (string.IsNullOrWhiteSpace(subject))
                rowErrors.Add("主题不能为空");
            else if (subject.Length > 998 || subject.Contains('\r') || subject.Contains('\n'))
                rowErrors.Add("主题过长或包含换行");
            if (string.IsNullOrWhiteSpace(body))
                rowErrors.Add("正文不能为空");

            if (!TryParseScheduledAt(time, utcNow, localTimeZone, out var scheduledAtUtc))
                rowErrors.Add("时间格式不正确，请使用 yyyy-MM-dd HH:mm:ss；留空表示立即发送");

            if (rowErrors.Count > 0)
            {
                errors.Add(new OutboundMailTaskCsvError(lineNumber, string.Join("；", rowErrors) + "。"));
                continue;
            }

            rows.Add(new OutboundMailTaskCsvRow(
                lineNumber,
                scheduledAtUtc,
                sender,
                recipient,
                subject,
                body));
        }

        return new(rows, errors);
    }

    private static Dictionary<string, int> CreateHeaderIndex(
        IReadOnlyList<string> headers,
        ICollection<OutboundMailTaskCsvError> errors)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            var normalized = headers[i].Trim().TrimStart('\uFEFF');
            if (!HeaderAliases.TryGetValue(normalized, out var canonical))
                continue;
            if (!index.TryAdd(canonical, i))
                errors.Add(new OutboundMailTaskCsvError(1, $"表头“{headers[i]}”重复。"));
        }

        var missing = new[] { "time", "sender", "recipient", "subject", "body" }
            .Where(required => !index.ContainsKey(required))
            .Select(required => required switch
            {
                "time" => "时间",
                "sender" => "发件邮箱",
                "recipient" => "收件邮箱",
                "subject" => "主题",
                _ => "正文"
            })
            .ToArray();
        if (missing.Length > 0)
            errors.Add(new OutboundMailTaskCsvError(1, $"CSV 缺少必需表头：{string.Join("、", missing)}。"));

        return index;
    }

    private static bool TryParseScheduledAt(
        string value,
        DateTime utcNow,
        TimeZoneInfo localTimeZone,
        out DateTime scheduledAtUtc)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            scheduledAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            return true;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
                out var timestampWithOffset) &&
            (value.EndsWith('Z') || value.Contains('+') || HasOffsetSuffix(value)))
        {
            scheduledAtUtc = timestampWithOffset.UtcDateTime;
            return true;
        }

        if (!DateTime.TryParseExact(
                value,
                TimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var localTime))
        {
            scheduledAtUtc = default;
            return false;
        }

        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        if (localTimeZone.IsInvalidTime(localTime))
        {
            scheduledAtUtc = default;
            return false;
        }

        scheduledAtUtc = TimeZoneInfo.ConvertTimeToUtc(localTime, localTimeZone);
        return true;
    }

    private static bool HasOffsetSuffix(string value)
    {
        var separator = value.LastIndexOfAny(['+', '-']);
        return separator > value.IndexOf('T') && separator >= value.Length - 6;
    }

    private static bool IsValidEmail(string value)
    {
        try
        {
            return new MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
