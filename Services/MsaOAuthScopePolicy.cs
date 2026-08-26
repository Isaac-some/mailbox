namespace MailArchiver.Services;

public static class MsaOAuthScopePolicy
{
    public const string Imap = "https://outlook.office.com/IMAP.AccessAsUser.All";
    public const string Smtp = "https://outlook.office.com/SMTP.Send";

    public static readonly string[] RequestedScopes =
        [Imap, Smtp, "offline_access", "openid", "profile", "email"];

    public static bool CanSend(string? grantedScopes)
    {
        if (string.IsNullOrWhiteSpace(grantedScopes))
            return false;

        return grantedScopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(Smtp, StringComparer.OrdinalIgnoreCase);
    }

    public static bool CanAttemptSend(string? refreshToken, string? grantedScopes)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return false;

        // A vendor import has no access-token response yet, so its actual scopes
        // are unknown until the first refresh. Once known, require SMTP.Send.
        return string.IsNullOrWhiteSpace(grantedScopes) || CanSend(grantedScopes);
    }
}
