using MailArchiver.Models;

namespace MailArchiver.Services.MailProviders;

public sealed class GmailMailProviderModule : PasswordAndOAuthMailProviderModule
{
    private static readonly ExternalOAuthSettings OAuthSettings = new(
        "Gmail",
        "https://oauth2.googleapis.com/token",
        RequiresClientSecret: false,
        RequiresRedirectUri: false);

    public GmailMailProviderModule(
        IExternalOAuthTokenManager tokenManager,
        ICredentialEncryptionService credentialEncryption)
        : base(tokenManager, credentialEncryption) { }

    public override MailProviderKind Kind => MailProviderKind.Gmail;
    public override string DisplayName => "Gmail";
    public override ImapEndpoint GetIncomingEndpoint(MailAccount account) => new("imap.gmail.com", 993, true);
    protected override string GetSmtpHost(MailAccount account) => "smtp.gmail.com";
    protected override bool SmtpSavesSentCopy => true;
    protected override ExternalOAuthSettings OAuth => OAuthSettings;

    public override bool SupportsAddress(string emailAddress)
        => DomainOf(emailAddress) is "gmail.com" or "googlemail.com";
}
