namespace MailArchiver.Models;

public enum MailCredentialKind
{
    Unknown,
    GoogleAppPassword,
    OAuth2RefreshToken,
    ImapPassword,
    SmtpPassword,
    SharedMailPassword
}

public enum MailCredentialScope
{
    Unknown,
    Imap,
    Smtp,
    ImapAndSmtp
}
