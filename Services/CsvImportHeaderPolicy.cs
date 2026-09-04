namespace MailArchiver.Services;

/// <summary>
/// The mailbox hand-off contract has exactly four fields. CSV aliases are kept
/// deliberately small so the downloadable file and the upstream API cannot
/// silently drift into different schemas.
/// </summary>
public static class CsvImportHeaderPolicy
{
    public static readonly string[] CanonicalHeaders = ["email", "domain", "credential", "client_id"];

    private static readonly IReadOnlyDictionary<string, string[]> Aliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = ["email", "邮箱", "邮箱（必填）"],
            ["domain"] = ["domain", "域名", "域名（可选）"],
            ["credential"] = ["credential", "授权码", "授权凭据", "授权码（必填）", "授权凭据（必填）"],
            ["client_id"] = ["client_id", "client id", "cilent id", "cilent_id", "客户端id", "client id（可选）"]
        };

    private static readonly IReadOnlyDictionary<string, string[]> FlexibleAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = ["email", "e-mail", "mail", "email address", "邮箱", "邮箱地址", "邮箱账号", "账号", "账号邮箱"],
            // IMAP 授权码 is intentionally first: exports often contain both
            // the ordinary password and the final 16-character app password.
            ["credential"] = [
                "imap授权码", "imap授权凭据", "imap app password", "imap密码",
                "smtp授权码", "smtp授权凭据", "smtp app password", "smtp密码",
                "授权码", "授权凭据", "应用专用密码", "app password", "credential",
                "password", "密码", "refresh token", "refresh_token", "token"],
            ["domain"] = ["domain", "域名", "邮箱域名"],
            ["client_id"] = ["client_id", "client id", "cilent id", "cilent_id", "客户端id"]
        };

    public static bool TryCreateCanonicalIndex(
        IReadOnlyList<string> headers,
        out Dictionary<string, int> columns)
    {
        columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (headers.Count != CanonicalHeaders.Length)
            return false;

        for (var index = 0; index < headers.Count; index++)
        {
            var header = NormalizeHeader(headers[index]);
            var canonical = Aliases.FirstOrDefault(pair =>
                pair.Value.Any(alias => NormalizeHeader(alias) == header)).Key;
            if (string.IsNullOrEmpty(canonical) || !columns.TryAdd(canonical, index))
                return false;
        }

        return CanonicalHeaders.All(columns.ContainsKey);
    }

    /// <summary>Accepts exports with extra columns and common Chinese labels.</summary>
    public static bool TryCreateFlexibleIndex(
        IReadOnlyList<string> headers,
        out Dictionary<string, int> columns)
    {
        columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var credentialCandidates = new List<(int Index, int Priority)>();

        for (var index = 0; index < headers.Count; index++)
        {
            var normalized = NormalizeHeader(headers[index]);
            if (string.IsNullOrEmpty(normalized))
                continue;

            if (!columns.ContainsKey("email") && Matches(normalized, "email"))
                columns["email"] = index;
            if (!columns.ContainsKey("domain") && Matches(normalized, "domain"))
                columns["domain"] = index;
            if (!columns.ContainsKey("client_id") && Matches(normalized, "client_id"))
                columns["client_id"] = index;
            if (Matches(normalized, "credential"))
                credentialCandidates.Add((index, CredentialPriority(normalized)));
        }

        var credential = credentialCandidates
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();
        if (credentialCandidates.Count > 0)
            columns["credential"] = credential.Index;

        return columns.ContainsKey("email") && columns.ContainsKey("credential");
    }

    private static bool Matches(string normalized, string canonical)
        => FlexibleAliases[canonical].Any(alias => NormalizeHeader(alias) == normalized);

    private static int CredentialPriority(string normalized)
        => normalized.Contains("imap", StringComparison.OrdinalIgnoreCase) ? 100
            : normalized.Contains("smtp", StringComparison.OrdinalIgnoreCase) ? 95
            : normalized.Contains("授权", StringComparison.OrdinalIgnoreCase) ? 90
            : normalized.Contains("应用专用", StringComparison.OrdinalIgnoreCase) || normalized.Contains("app password", StringComparison.OrdinalIgnoreCase) ? 80
            : normalized.Contains("credential", StringComparison.OrdinalIgnoreCase) ? 70
            : normalized.Contains("password", StringComparison.OrdinalIgnoreCase) || normalized == "密码" ? 60
            : normalized.Contains("refresh", StringComparison.OrdinalIgnoreCase) ? 50
            : 40;

    private static string NormalizeHeader(string value)
    {
        var header = (value ?? string.Empty).Trim().TrimStart('\uFEFF');
        var parenthesis = header.IndexOfAny(['（', '(']);
        if (parenthesis >= 0)
            header = header[..parenthesis];
        return string.Concat(header.Where(character => !char.IsWhiteSpace(character)))
            .Replace('-', '_')
            .ToLowerInvariant();
    }
}
