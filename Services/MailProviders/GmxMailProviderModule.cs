using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.MailProviders;

public sealed class GmxMailProviderModule : PasswordAndOAuthMailProviderModule
{
    public GmxMailProviderModule(
        IExternalOAuthTokenManager tokenManager,
        ICredentialEncryptionService credentialEncryption,
        INetworkMailProxyFactory? networkMail = null)
        : base(tokenManager, credentialEncryption, networkMail) { }

    public override MailProviderKind Kind => MailProviderKind.Gmx;
    public override string DisplayName => "GMX";
    public override ImapEndpoint GetIncomingEndpoint(MailAccount account)
        => DomainOf(account.EmailAddress) == "gmx.com"
            ? new ImapEndpoint("imap.gmx.com", 993, true)
            : new ImapEndpoint("imap.gmx.net", 993, true);

    protected override string GetSmtpHost(MailAccount account)
        => DomainOf(account.EmailAddress) == "gmx.com" ? "mail.gmx.com" : "mail.gmx.net";

    public override bool SupportsAddress(string emailAddress)
        => DomainOf(emailAddress) is "gmx.com" or "gmx.net" or "gmx.de";

    public override MailAccountCapabilities Inspect(MailAccount account)
    {
        EnsureIdentity(account);
        var ready = HasPassword(account);
        var smtpVerified = account.CredentialScope is MailCredentialScope.ImapAndSmtp
            or MailCredentialScope.Smtp;
        var requiredAction = ready switch
        {
            false => "请补充 GMX 应用专用密码。",
            true when account.CredentialScope == MailCredentialScope.Imap
                || string.Equals(account.CredentialDetectionStatus, "ImapVerified", StringComparison.OrdinalIgnoreCase)
                => "已验证收件，但发件 SMTP 尚未验证；请刷新收件箱后重试。",
            _ => null
        };
        return new MailAccountCapabilities(
            ready,
            ready && (smtpVerified || account.CredentialScope == MailCredentialScope.Unknown),
            requiredAction);
    }
}
