using System.Net.Mail;

namespace MailArchiver.Services;

public sealed record ExternalImportedAccount(
    int LineNumber,
    string Email,
    string? AppPassword,
    string? ClientId,
    string? ClientSecret,
    string? RefreshToken,
    string? RedirectUri);

public sealed record ExternalMailAccountTextError(int LineNumber, string Reason);

public sealed record ExternalMailAccountTextParseResult(
    IReadOnlyList<ExternalImportedAccount> Accounts,
    IReadOnlyList<ExternalMailAccountTextError> Errors);

public static class ExternalMailAccountTextParser
{
    public static ExternalMailAccountTextParseResult Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var accounts = new List<ExternalImportedAccount>();
        var errors = new List<ExternalMailAccountTextError>();
        string? line;
        var lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = line.Split('\t');
            var email = fields[0].Trim();
            if (!IsValidEmail(email) || !SupportedMailProviderPolicy.TryResolve(email, out _))
            {
                errors.Add(new ExternalMailAccountTextError(lineNumber, "仅支持 Gmail、Yahoo 和 GMX 邮箱地址。"));
                continue;
            }

            if (fields.Length == 2)
            {
                if (string.IsNullOrWhiteSpace(fields[1]))
                {
                    errors.Add(new ExternalMailAccountTextError(lineNumber, "应用专用密码不能为空。"));
                    continue;
                }

                accounts.Add(new ExternalImportedAccount(
                    lineNumber, email, fields[1], null, null, null, null));
                continue;
            }

            if (fields.Length is 3 or 4 or 5)
            {
                var clientId = fields[1].Trim();
                var clientSecret = fields.Length >= 4 ? fields[2].Trim() : null;
                var refreshToken = fields.Length == 3 ? fields[2].Trim() : fields[3].Trim();
                var redirectUri = fields.Length == 5 ? fields[4].Trim() : null;
                if (!ExternalOAuthProviderPolicy.TryResolve(email, out var provider))
                {
                    errors.Add(new ExternalMailAccountTextError(lineNumber, "GMX 官方不支持 OAuth，请提供邮箱和应用专用密码两列。"));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(refreshToken))
                {
                    errors.Add(new ExternalMailAccountTextError(lineNumber, "OAuth 的 Client ID 和 Refresh Token 不能为空。"));
                    continue;
                }
                if (provider.RequiresClientSecret && string.IsNullOrWhiteSpace(clientSecret))
                {
                    errors.Add(new ExternalMailAccountTextError(lineNumber, "Yahoo OAuth 必须提供 Client Secret。"));
                    continue;
                }
                if (provider.RequiresRedirectUri && string.IsNullOrWhiteSpace(redirectUri))
                {
                    errors.Add(new ExternalMailAccountTextError(lineNumber, "Yahoo OAuth 必须提供生成 Refresh Token 时使用的 Redirect URI（常见值为 oob）。"));
                    continue;
                }

                accounts.Add(new ExternalImportedAccount(
                    lineNumber, email, null, clientId, clientSecret, refreshToken, redirectUri));
                continue;
            }

            errors.Add(new ExternalMailAccountTextError(
                lineNumber,
                "每行必须是 2 列（邮箱、应用专用密码）、3/4 列（Gmail OAuth）或 5 列（Yahoo OAuth：邮箱、Client ID、Client Secret、Refresh Token、Redirect URI），并用 Tab 分隔。"));
        }

        return new ExternalMailAccountTextParseResult(accounts, errors);
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
