using System.Net.Mail;

namespace MailArchiver.Services;

/// <summary>
/// Resolves the built-in provider endpoints and a conservative custom-domain
/// fallback. User-supplied CSV values never override these host construction
/// rules or disable TLS.
/// </summary>
public static class SupportedMailProviderPolicy
{
    public static bool TryResolve(string? emailAddress, out ImapProviderPreset preset)
    {
        preset = default!;

        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            return false;
        }

        try
        {
            var domain = new MailAddress(emailAddress.Trim()).Host.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(domain))
                return false;

            if (domain.StartsWith("yahoo.", StringComparison.Ordinal))
            {
                preset = new ImapProviderPreset(
                    "Yahoo", "imap.mail.yahoo.com", 993, true,
                    "smtp.mail.yahoo.com", 587, true);
                return true;
            }

            preset = domain switch
            {
                "gmail.com" or "googlemail.com" => new ImapProviderPreset(
                    "Gmail", "imap.gmail.com", 993, true,
                    "smtp.gmail.com", 587, true, SmtpSavesSentCopy: true),
                "gmx.com" => new ImapProviderPreset(
                    "GMX", "imap.gmx.com", 993, true,
                    "mail.gmx.com", 587, true),
                "gmx.net" or "gmx.de" => new ImapProviderPreset(
                    "GMX", "imap.gmx.net", 993, true,
                    "mail.gmx.net", 587, true),
                "outlook.com" or "hotmail.com" or "live.com" or "msn.com" => new ImapProviderPreset(
                    "Outlook", "outlook.office365.com", 993, true,
                    "smtp-mail.outlook.com", 587, true),
                _ => new ImapProviderPreset(
                    "自定义域名", $"imap.{domain}", 993, true,
                    $"smtp.{domain}", 587, true)
            };

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record ImapProviderPreset(
    string Provider,
    string ImapServer,
    int ImapPort,
    bool UseSsl,
    string SmtpServer,
    int SmtpPort,
    bool UseStartTls,
    bool SmtpSavesSentCopy = false);
