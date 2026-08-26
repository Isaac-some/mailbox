using MailArchiver.Models;

namespace MailArchiver.Services.MailProviders;

public sealed class GmxMailProviderModule : PasswordAndOAuthMailProviderModule
{
    public GmxMailProviderModule(
        IExternalOAuthTokenManager tokenManager,
        ICredentialEncryptionService credentialEncryption)
        : base(tokenManager, credentialEncryption) { }

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
        return new MailAccountCapabilities(
            ready,
            ready,
            ready ? null : "请补充 GMX 应用专用密码。");
    }
}
