using System.Net.Mail;

namespace MailArchiver.Services;

/// <summary>
/// Fixed OAuth endpoints for consumer mail providers supported by the local app.
/// Endpoints are never accepted from uploaded files, preventing arbitrary token requests.
/// </summary>
public static class ExternalOAuthProviderPolicy
{
    public static bool TryResolve(string? emailAddress, out ExternalOAuthProviderPreset preset)
    {
        preset = default!;
        if (string.IsNullOrWhiteSpace(emailAddress))
            return false;

        try
        {
            var domain = new MailAddress(emailAddress.Trim()).Host.ToLowerInvariant();
            if (domain.StartsWith("yahoo.", StringComparison.Ordinal))
            {
                preset = new ExternalOAuthProviderPreset(
                    "Yahoo",
                    "https://api.login.yahoo.com/oauth2/get_token",
                    RequiresClientSecret: true,
                    RequiresRedirectUri: true);
                return true;
            }

            if (domain is "gmail.com" or "googlemail.com")
            {
                preset = new ExternalOAuthProviderPreset(
                    "Google",
                    "https://oauth2.googleapis.com/token",
                    RequiresClientSecret: false,
                    RequiresRedirectUri: false);
                return true;
            }

            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool HasUsableCredentials(MailAccount account)
    {
        if (!TryResolve(account.EmailAddress, out var preset))
            return false;

        return !string.IsNullOrWhiteSpace(account.ClientId)
            && !string.IsNullOrWhiteSpace(account.OAuthRefreshToken)
            && (!preset.RequiresClientSecret || !string.IsNullOrWhiteSpace(account.ClientSecret))
            && (!preset.RequiresRedirectUri || !string.IsNullOrWhiteSpace(account.OAuthRedirectUri));
    }
}

public sealed record ExternalOAuthProviderPreset(
    string Provider,
    string TokenEndpoint,
    bool RequiresClientSecret,
    bool RequiresRedirectUri);
