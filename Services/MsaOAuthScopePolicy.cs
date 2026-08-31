namespace MailArchiver.Services;

public static class MsaOAuthScopePolicy
{
    public const string Imap = "https://outlook.office.com/IMAP.AccessAsUser.All";
    public const string SmtpSend = "https://outlook.office.com/SMTP.Send";
    public const string GraphMailSend = "https://graph.microsoft.com/Mail.Send";

    public static readonly string[] RequestedScopes =
        [Imap, SmtpSend, GraphMailSend, "offline_access", "openid", "profile", "email"];

    public static readonly string[] ImapRefreshScopes =
        [Imap, "offline_access"];

    public static readonly string[] GraphRefreshScopes =
        [GraphMailSend, "offline_access"];

    public static readonly string[] SmtpRefreshScopes =
        [SmtpSend, "offline_access"];

    public static bool CanSend(string? grantedScopes)
    {
        if (string.IsNullOrWhiteSpace(grantedScopes))
            return false;

        return grantedScopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(scope => scope.Equals("Mail.Send", StringComparison.OrdinalIgnoreCase)
                          || scope.Equals(GraphMailSend, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanAttemptSend(string? refreshToken, string? _)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return false;

        // OAuthGrantedScopes describes the current IMAP access token, not the Graph token.
        // The Graph Mail.Send permission is verified when a Graph token is acquired.
        return true;
    }
}
