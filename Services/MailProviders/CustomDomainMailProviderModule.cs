using System.Net.Mail;
using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.MailProviders;

/// <summary>
/// Module for domains that are not one of the built-in consumer providers.
/// Endpoints are initially conventional and can be replaced by the bounded
/// Mozilla Autoconfig discovery service before the first connection check.
/// </summary>
public sealed class CustomDomainMailProviderModule : PasswordAndOAuthMailProviderModule
{
    private static readonly HashSet<string> BuiltInDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "msn.com",
        "gmx.com", "gmx.net", "gmx.de"
    };

    public CustomDomainMailProviderModule(
        IExternalOAuthTokenManager tokenManager,
        ICredentialEncryptionService credentialEncryption,
        IOptions<MailProxyOptions>? mailProxyOptions = null)
        : base(tokenManager, credentialEncryption, mailProxyOptions) { }

    public override MailProviderKind Kind => MailProviderKind.Custom;
    public override string DisplayName => "自定义域名";

    public override bool SupportsAddress(string emailAddress)
    {
        var domain = DomainOf(emailAddress);
        return !string.IsNullOrWhiteSpace(domain) &&
            !BuiltInDomains.Contains(domain) &&
            !domain.StartsWith("yahoo.", StringComparison.OrdinalIgnoreCase);
    }

    public override ImapEndpoint GetIncomingEndpoint(MailAccount account)
        => new(
            string.IsNullOrWhiteSpace(account.ImapServer)
                ? $"imap.{DomainOf(account.EmailAddress)}"
                : account.ImapServer,
            account.ImapPort ?? 993,
            account.UseSSL);

    protected override string GetSmtpHost(MailAccount account)
        => string.IsNullOrWhiteSpace(account.SmtpServer)
            ? $"smtp.{DomainOf(account.EmailAddress)}"
            : account.SmtpServer;

    protected override int GetSmtpPort(MailAccount account)
        => account.SmtpPort ?? base.GetSmtpPort(account);

    protected override MailKit.Security.SecureSocketOptions GetSmtpSocketOptions(MailAccount account)
    {
        if (account.SmtpUseSSL == false)
            return MailKit.Security.SecureSocketOptions.None;
        return GetSmtpPort(account) == 465
            ? MailKit.Security.SecureSocketOptions.SslOnConnect
            : MailKit.Security.SecureSocketOptions.StartTls;
    }
}
