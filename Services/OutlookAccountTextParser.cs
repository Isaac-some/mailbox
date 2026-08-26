using System.Net.Mail;

namespace MailArchiver.Services;

public sealed record OutlookImportedAccount(
    int LineNumber,
    string Email,
    string ClientId,
    string RefreshToken);

public sealed record OutlookAccountTextError(int LineNumber, string Reason);

public sealed record OutlookAccountTextParseResult(
    IReadOnlyList<OutlookImportedAccount> Accounts,
    IReadOnlyList<OutlookAccountTextError> Errors);

/// <summary>
/// Parses the headerless tab-separated Outlook credential export supplied by
/// mailbox vendors: email, password, client id, refresh token. The Microsoft
/// password is deliberately validated but never returned or persisted; IMAP
/// and SMTP both authenticate with the OAuth refresh token.
/// </summary>
public static class OutlookAccountTextParser
{
    public static bool LooksLikeFormat(string? firstNonEmptyLine)
    {
        var fields = firstNonEmptyLine?.Split('\t');
        return fields?.Length == 4 && Guid.TryParseExact(fields[2].Trim(), "D", out _);
    }

    public static OutlookAccountTextParseResult Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var accounts = new List<OutlookImportedAccount>();
        var errors = new List<OutlookAccountTextError>();
        string? line;
        var lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = line.Split('\t');
            if (fields.Length != 4)
            {
                errors.Add(new OutlookAccountTextError(lineNumber, "每行必须是邮箱、密码、Client ID、Refresh Token 四列，并用 Tab 分隔。"));
                continue;
            }

            var email = fields[0].Trim();
            var password = fields[1];
            var clientId = fields[2].Trim();
            var refreshToken = fields[3].Trim();

            if (!IsValidEmail(email))
            {
                errors.Add(new OutlookAccountTextError(lineNumber, "邮箱地址格式不正确。"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                errors.Add(new OutlookAccountTextError(lineNumber, "密码列不能为空。"));
                continue;
            }

            if (!Guid.TryParseExact(clientId, "D", out _))
            {
                errors.Add(new OutlookAccountTextError(lineNumber, "Client ID 格式不正确。"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                errors.Add(new OutlookAccountTextError(lineNumber, "Refresh Token 不能为空。"));
                continue;
            }

            accounts.Add(new OutlookImportedAccount(lineNumber, email, clientId, refreshToken));
        }

        return new OutlookAccountTextParseResult(accounts, errors);
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
