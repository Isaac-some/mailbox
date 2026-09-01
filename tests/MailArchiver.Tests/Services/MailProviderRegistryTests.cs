using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;

namespace MailArchiver.Tests.Services;

public class MailProviderRegistryTests
{
    private static MailProviderRegistry CreateRegistry()
        => new([
            new GmailMailProviderModule(null!, null!),
            new YahooMailProviderModule(null!, null!),
            new GmxMailProviderModule(null!, null!),
            new OutlookMailProviderModule(null!, null!),
            new CustomDomainMailProviderModule(null!, null!)
        ]);

    [Theory]
    [InlineData("one@gmail.com", MailProviderKind.Gmail)]
    [InlineData("one@googlemail.com", MailProviderKind.Gmail)]
    [InlineData("one@yahoo.co.jp", MailProviderKind.Yahoo)]
    [InlineData("one@gmx.de", MailProviderKind.Gmx)]
    [InlineData("one@outlook.com", MailProviderKind.Outlook)]
    [InlineData("one@corp.example", MailProviderKind.Custom)]
    public void Detect_routes_supported_addresses_to_one_module(string email, MailProviderKind expected)
        => Assert.Equal(expected, CreateRegistry().Detect(email).Kind);

    [Fact]
    public void Runtime_routing_uses_persisted_identity_not_email_suffix()
    {
        var account = new MailAccount
        {
            EmailAddress = "looks-like-yahoo@yahoo.com",
            MailProviderKind = MailProviderKind.Gmail
        };

        Assert.Equal(MailProviderKind.Gmail, CreateRegistry().For(account).Kind);
    }

    [Theory]
    [InlineData(MailProviderKind.Gmail, "one@gmail.com", "imap.gmail.com", ProviderType.IMAP)]
    [InlineData(MailProviderKind.Yahoo, "one@yahoo.com", "imap.mail.yahoo.com", ProviderType.IMAP)]
    [InlineData(MailProviderKind.Gmx, "one@gmx.com", "imap.gmx.com", ProviderType.IMAP)]
    [InlineData(MailProviderKind.Outlook, "one@outlook.com", "outlook.office365.com", ProviderType.MSA)]
    public void PrepareAccount_sets_explicit_identity_and_provider_owned_endpoint(
        MailProviderKind kind,
        string email,
        string expectedHost,
        ProviderType expectedTransport)
    {
        var account = new MailAccount { EmailAddress = email };

        CreateRegistry().For(kind).PrepareAccount(account);

        Assert.Equal(kind, account.MailProviderKind);
        Assert.Equal(expectedTransport, account.Provider);
        Assert.Equal(expectedHost, account.ImapServer);
        Assert.Equal(993, account.ImapPort);
        Assert.True(account.UseSSL);
    }

    [Fact]
    public void Each_module_owns_its_send_capability_rules()
    {
        var registry = CreateRegistry();
        var gmail = new MailAccount
        {
            EmailAddress = "one@gmail.com",
            MailProviderKind = MailProviderKind.Gmail,
            ClientId = "client",
            OAuthRefreshToken = "refresh"
        };
        var yahoo = new MailAccount
        {
            EmailAddress = "one@yahoo.com",
            MailProviderKind = MailProviderKind.Yahoo,
            ClientId = "client",
            OAuthRefreshToken = "refresh"
        };
        var gmx = new MailAccount
        {
            EmailAddress = "one@gmx.com",
            MailProviderKind = MailProviderKind.Gmx,
            ClientId = "client",
            OAuthRefreshToken = "refresh"
        };
        var outlook = new MailAccount
        {
            EmailAddress = "one@outlook.com",
            MailProviderKind = MailProviderKind.Outlook,
            OAuthRefreshToken = "refresh",
            OAuthGrantedScopes = MsaOAuthScopePolicy.Imap
        };

        Assert.True(registry.For(gmail).Inspect(gmail).CanSend);
        Assert.False(registry.For(yahoo).Inspect(yahoo).CanSend);
        Assert.False(registry.For(gmx).Inspect(gmx).CanSend);
        Assert.True(registry.For(outlook).Inspect(outlook).CanSend);
    }

    [Fact]
    public void Custom_module_uses_conventional_endpoints_until_discovery_overrides_them()
    {
        var account = new MailAccount { EmailAddress = "one@corp.example", UseSSL = true };

        CreateRegistry().For(MailProviderKind.Custom).PrepareAccount(account);

        Assert.Equal("imap.corp.example", account.ImapServer);
        Assert.Equal(993, account.ImapPort);
        Assert.True(account.UseSSL);
    }
}
