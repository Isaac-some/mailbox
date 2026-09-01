using System.Net.Mail;
using MailArchiver.Models;

namespace MailArchiver.Services;

public sealed record UnifiedImportedMailAccount(
    int LineNumber,
    MailProviderKind Provider,
    string Email,
    string? AppPassword,
    string? ClientId,
    string? ClientSecret,
    string? RefreshToken,
    string? RedirectUri);

public sealed record UnifiedMailAccountTextError(int LineNumber, string Reason);

public sealed record UnifiedMailAccountTextParseResult(
    IReadOnlyList<UnifiedImportedMailAccount> Accounts,
    IReadOnlyList<UnifiedMailAccountTextError> Errors);

/// <summary>
/// Parses headerless, tab-separated account exports one row at a time. Provider
/// detection is deliberately row-local so Outlook and non-Microsoft accounts can
/// be mixed in one upload.
/// </summary>
public static class UnifiedMailAccountTextParser
{
    public static UnifiedMailAccountTextParseResult Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var accounts = new List<UnifiedImportedMailAccount>();
        var errors = new List<UnifiedMailAccountTextError>();
        string? line;
        var lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = line.Split('\t');
            var email = fields[0].Trim();
            if (!TryDetectProvider(email, out var provider))
            {
                errors.Add(new(lineNumber, "邮箱地址格式不正确。"));
                continue;
            }

            if (provider == MailProviderKind.Outlook)
            {
                ParseOutlook(fields, email, lineNumber, accounts, errors);
                continue;
            }

            ParseExternal(provider, fields, email, lineNumber, accounts, errors);
        }

        return new UnifiedMailAccountTextParseResult(accounts, errors);
    }

    private static void ParseOutlook(
        string[] fields,
        string email,
        int lineNumber,
        ICollection<UnifiedImportedMailAccount> accounts,
        ICollection<UnifiedMailAccountTextError> errors)
    {
        if (fields.Length == 2)
        {
            var passwordOnly = NullIfWhiteSpace(fields[1], preserveWhitespace: true);
            if (passwordOnly is null)
            {
                errors.Add(new(lineNumber, "Outlook 密码不能为空。"));
                return;
            }
            accounts.Add(new(lineNumber, MailProviderKind.Outlook, email, passwordOnly,
                null, null, null, null));
            return;
        }

        if (fields.Length != 4)
        {
            errors.Add(new(lineNumber, "Outlook 使用邮箱、密码两列，或邮箱、密码、Client ID、Refresh Token 四列。"));
            return;
        }

        var clientId = fields[2].Trim();
        var refreshToken = fields[3].Trim();
        if (!Guid.TryParseExact(clientId, "D", out _))
        {
            errors.Add(new(lineNumber, "Outlook Client ID 格式不正确。"));
            return;
        }
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            errors.Add(new(lineNumber, "Outlook Refresh Token 不能为空。"));
            return;
        }

        accounts.Add(new(lineNumber, MailProviderKind.Outlook, email, NullIfWhiteSpace(fields[1], preserveWhitespace: true),
            clientId, null, refreshToken, null));
    }

    private static void ParseExternal(
        MailProviderKind provider,
        string[] fields,
        string email,
        int lineNumber,
        ICollection<UnifiedImportedMailAccount> accounts,
        ICollection<UnifiedMailAccountTextError> errors)
    {
        if (fields.Length == 2)
        {
            if (string.IsNullOrWhiteSpace(fields[1]))
            {
                errors.Add(new(lineNumber, "应用专用密码不能为空。"));
                return;
            }

            accounts.Add(new(lineNumber, provider, email, fields[1], null, null, null, null));
            return;
        }

        if (provider == MailProviderKind.Gmx)
        {
            errors.Add(new(lineNumber, "GMX 官方不支持 OAuth，请提供邮箱和应用专用密码两列。"));
            return;
        }

        if (provider == MailProviderKind.Custom)
        {
            errors.Add(new(lineNumber, "自定义域名请先提供 IMAP/SMTP 密码；OAuth2 需要先配置该域名的 Token 地址。"));
            return;
        }

        string? appPassword = null;
        string clientId;
        string? clientSecret;
        string refreshToken;
        string? redirectUri;

        if (fields.Length == 6)
        {
            appPassword = NullIfWhiteSpace(fields[1], preserveWhitespace: true);
            clientId = fields[2].Trim();
            clientSecret = NullIfWhiteSpace(fields[3]);
            refreshToken = fields[4].Trim();
            redirectUri = NullIfWhiteSpace(fields[5]);
        }
        else if (provider == MailProviderKind.Gmail && fields.Length == 3)
        {
            clientId = fields[1].Trim();
            clientSecret = null;
            refreshToken = fields[2].Trim();
            redirectUri = null;
        }
        else if (provider == MailProviderKind.Yahoo && fields.Length == 5)
        {
            clientId = fields[1].Trim();
            clientSecret = NullIfWhiteSpace(fields[2]);
            refreshToken = fields[3].Trim();
            redirectUri = NullIfWhiteSpace(fields[4]);
        }
        else
        {
            errors.Add(new(lineNumber,
                provider == MailProviderKind.Gmail
                    ? "Gmail 使用 2 列应用专用密码、3 列 OAuth，或 6 列双凭据格式。"
                    : "Yahoo 使用 2 列应用专用密码、5 列 OAuth，或 6 列双凭据格式。"));
            return;
        }

        var requiresYahooFields = provider == MailProviderKind.Yahoo;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(refreshToken) ||
            (requiresYahooFields && (string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(redirectUri))))
        {
            errors.Add(new(lineNumber,
                requiresYahooFields
                    ? "Yahoo OAuth 必须提供 Client ID、Client Secret、Refresh Token 和 Redirect URI。"
                    : "Gmail OAuth 必须提供 Client ID 和 Refresh Token。"));
            return;
        }

        accounts.Add(new(lineNumber, provider, email, appPassword,
            clientId, clientSecret, refreshToken, redirectUri));
    }

    private static bool TryDetectProvider(string email, out MailProviderKind provider)
    {
        provider = default;
        try
        {
            var domain = new MailAddress(email).Host.ToLowerInvariant();
            if (domain is "gmail.com" or "googlemail.com") provider = MailProviderKind.Gmail;
            else if (domain.StartsWith("yahoo.", StringComparison.Ordinal)) provider = MailProviderKind.Yahoo;
            else if (domain is "gmx.com" or "gmx.net" or "gmx.de") provider = MailProviderKind.Gmx;
            else if (domain is "outlook.com" or "hotmail.com" or "live.com" or "msn.com") provider = MailProviderKind.Outlook;
            else provider = MailProviderKind.Custom;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string? NullIfWhiteSpace(string value, bool preserveWhitespace = false)
        => string.IsNullOrWhiteSpace(value) ? null : preserveWhitespace ? value : value.Trim();
}
