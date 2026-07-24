using System.Net.Mail;

namespace MailArchiver.Services;

/// <summary>
/// The only remote mail providers permitted by the local archive deployment.
/// Keeping these settings in one policy prevents user supplied CSV values from
/// silently connecting to an unexpected server or disabling TLS.
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

            if (domain.StartsWith("yahoo.", StringComparison.Ordinal))
            {
                preset = new ImapProviderPreset("Yahoo", "imap.mail.yahoo.com", 993, true);
                return true;
            }

            preset = domain switch
            {
                "gmx.com" => new ImapProviderPreset("GMX", "imap.gmx.com", 993, true),
                "gmx.net" or "gmx.de" => new ImapProviderPreset("GMX", "imap.gmx.net", 993, true),
                _ => default!
            };

            return preset is not null;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed record ImapProviderPreset(string Provider, string ImapServer, int ImapPort, bool UseSsl);
