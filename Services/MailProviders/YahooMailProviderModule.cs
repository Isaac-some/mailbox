using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.MailProviders;

public sealed class YahooMailProviderModule : PasswordAndOAuthMailProviderModule
{
    private static readonly ExternalOAuthSettings OAuthSettings = new(
        "Yahoo",
        "https://api.login.yahoo.com/oauth2/get_token",
        RequiresClientSecret: true,
        RequiresRedirectUri: true);

    public YahooMailProviderModule(
        IExternalOAuthTokenManager tokenManager,
        ICredentialEncryptionService credentialEncryption,
        IOptions<MailProxyOptions>? mailProxyOptions = null)
        : base(tokenManager, credentialEncryption, mailProxyOptions) { }

    public override MailProviderKind Kind => MailProviderKind.Yahoo;
    public override string DisplayName => "Yahoo";
    public override ImapEndpoint GetIncomingEndpoint(MailAccount account) => new("imap.mail.yahoo.com", 993, true);
    protected override string GetSmtpHost(MailAccount account) => "smtp.mail.yahoo.com";
    protected override ExternalOAuthSettings OAuth => OAuthSettings;

    public override bool SupportsAddress(string emailAddress)
        => DomainOf(emailAddress)?.StartsWith("yahoo.", StringComparison.Ordinal) == true;
}
