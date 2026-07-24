namespace MailArchiver.Services;

/// <summary>
/// Defines the only CSV column mappings accepted by the local archive.
/// Provider connection settings never come from the file; only the address,
/// and read-only IMAP credential do.
/// </summary>
public static class CsvImportHeaderPolicy
{
    private static readonly string[] LegacyHeaders = ["email", "app_password", "group"];

    public static bool TryCreateCanonicalIndex(
        IReadOnlyList<string> headers,
        out Dictionary<string, int> columns)
    {
        columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (headers.Count == LegacyHeaders.Length &&
            headers.SequenceEqual(LegacyHeaders, StringComparer.OrdinalIgnoreCase))
        {
            for (var index = 0; index < LegacyHeaders.Length; index++)
            {
                columns[LegacyHeaders[index]] = index;
            }
            return true;
        }

        AddIfPresent(headers, columns, "邮箱", "email");
        AddIfPresent(headers, columns, "SMTP授权码", "app_password");
        return columns.ContainsKey("email") && columns.ContainsKey("app_password");
    }

    private static void AddIfPresent(
        IReadOnlyList<string> headers,
        IDictionary<string, int> columns,
        string sourceHeader,
        string canonicalHeader)
    {
        var index = headers
            .Select((header, position) => new { Header = header, Position = position })
            .FirstOrDefault(item => string.Equals(item.Header, sourceHeader, StringComparison.OrdinalIgnoreCase))
            ?.Position;

        if (index.HasValue)
        {
            columns[canonicalHeader] = index.Value;
        }
    }
}
