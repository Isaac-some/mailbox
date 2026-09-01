namespace MailArchiver.Services;

/// <summary>
/// Defines the only CSV column mappings accepted by the local archive.
/// Provider connection settings never come from the file; only the address and
/// provider-specific app-password or OAuth credentials do.
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

        AddAnyIfPresent(headers, columns, "email", "邮箱", "email");
        AddAnyIfPresent(headers, columns, "app_password", "SMTP授权码", "应用专用密码", "授权凭据", "credential", "app_password");
        // The upstream minimal contract may include a domain column. It is
        // intentionally accepted as metadata; provider settings still come from
        // the email domain and the fixed provider policy.
        AddAnyIfPresent(headers, columns, "domain", "域名", "域名（可选）", "domain");
        AddAnyIfPresent(headers, columns, "client_id", "Client ID", "client_id", "客户端ID");
        AddAnyIfPresent(headers, columns, "client_secret", "Client Secret", "client_secret", "客户端密钥");
        AddAnyIfPresent(headers, columns, "refresh_token", "Refresh Token", "refresh_token", "刷新令牌");
        AddAnyIfPresent(headers, columns, "redirect_uri", "Redirect URI", "redirect_uri", "回调地址");
        AddAnyIfPresent(headers, columns, "scopes", "Scopes", "scopes", "授权范围");
        return columns.ContainsKey("email") &&
            (columns.ContainsKey("app_password") ||
             (columns.ContainsKey("client_id") && columns.ContainsKey("refresh_token")));
    }

    private static void AddAnyIfPresent(
        IReadOnlyList<string> headers,
        IDictionary<string, int> columns,
        string canonicalHeader,
        params string[] aliases)
    {
        var index = headers
            .Select((header, position) => new { Header = header.Trim(), Position = position })
            .FirstOrDefault(item => aliases.Contains(item.Header, StringComparer.OrdinalIgnoreCase)
                || (canonicalHeader == "email" && item.Header.StartsWith("邮箱", StringComparison.OrdinalIgnoreCase))
                || (canonicalHeader == "app_password" && item.Header.StartsWith("授权凭据", StringComparison.OrdinalIgnoreCase))
                || (canonicalHeader == "domain" && item.Header.StartsWith("域名", StringComparison.OrdinalIgnoreCase))
                || (canonicalHeader == "client_id" && item.Header.StartsWith("Client ID", StringComparison.OrdinalIgnoreCase)))
            ?.Position;

        if (index.HasValue)
            columns[canonicalHeader] = index.Value;
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
