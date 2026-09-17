using System.Net.Mail;

namespace MailArchiver.Services;

public static class OutlookDomainPolicy
{
    private static readonly HashSet<string> Domains = new(StringComparer.OrdinalIgnoreCase)
    {
        "outlook.com",
        "hotmail.com",
        "live.com",
        "msn.com",
        "outlook.sg",
        "outlook.fr",
        "outlook.es",
        "outlook.com.au",
        "outlook.de",
        "outlook.jp"
    };

    public static bool IsOutlookDomain(string? domain) =>
        !string.IsNullOrWhiteSpace(domain) && Domains.Contains(domain.Trim().TrimStart('@'));

    public static bool IsOutlookAddress(string? emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
            return false;

        try
        {
            return IsOutlookDomain(new MailAddress(emailAddress.Trim()).Host);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
