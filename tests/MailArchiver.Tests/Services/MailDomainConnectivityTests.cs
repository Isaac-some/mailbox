using System.Net;
using System.Text;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailArchiver.Tests.Services;

public class MailDomainConnectivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mail-domain-test-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_root, "profiles.json");
    private MailDomainProfileStore Store() => new(StorePath);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData("gmx.es")]
    [InlineData("gmx.fr")]
    [InlineData("unknown-provider.example")]
    [InlineData("department.company.example")]
    public async Task Arbitrary_domains_are_discovered_without_adding_a_provider_module(string domain)
    {
        var store = Store();
        var http = new ConfigHttpFactory(_ => Config(domain));
        var account = Account(domain);
        await Discovery(http, store).DiscoverAsync(account);
        Assert.Equal("imap.provider.example", account.ImapServer);
        Assert.Equal("smtp.provider.example", account.SmtpServer);
        Assert.Equal("Discovered", account.EndpointDiscoveryStatus);
        Assert.Null(account.CredentialLastCheckedAt);
        Assert.Null(store.Find(domain)!.IncomingVerifiedAt);
        Assert.Equal("encrypted-secret", account.Password);
        Assert.All(http.Requests, u => Assert.DoesNotContain("secret", u.ToString()));
    }

    [Fact]
    public async Task Public_database_fixes_the_supplied_gmx_es_host_and_is_reused_after_restart()
    {
        var http = new ConfigHttpFactory(uri => uri.Host == "autoconfig.thunderbird.net" ? Config("gmx.es", "imap.gmx.com", "mail.gmx.com") : null);
        var first = Account("gmx.es");
        await Discovery(http, Store()).DiscoverAsync(first);
        Assert.Equal(3, http.Requests.Count);
        Assert.Equal("imap.gmx.com", first.ImapServer);
        var storeAfterRestart = Store();
        var offline = new ConfigHttpFactory(_ => throw new HttpRequestException("offline"));
        var second = Account("gmx.es");
        second.EmailAddress = "different@gmx.es";
        await Discovery(offline, storeAfterRestart).DiscoverAsync(second);
        Assert.Empty(offline.Requests);
        Assert.Equal("different@gmx.es", second.Username);
        Assert.Equal("imap.gmx.com", second.ImapServer);
        Assert.Equal("ThunderbirdISPDB", storeAfterRestart.Find("GMX.ES")!.Source);
        Assert.Null(second.CredentialLastCheckedAt);
    }

    [Theory]
    [InlineData("SSL", 993, SecureSocketOptions.SslOnConnect)]
    [InlineData("STARTTLS", 143, SecureSocketOptions.StartTls)]
    [InlineData("SSL", 1993, SecureSocketOptions.SslOnConnect)]
    [InlineData("STARTTLS", 1143, SecureSocketOptions.StartTls)]
    public async Task Encryption_mode_is_preserved_instead_of_guessed_from_port(string socket, int port, SecureSocketOptions expected)
    {
        var store = Store();
        var http = new ConfigHttpFactory(_ => Config("corp.example", socket: socket, port: port));
        var account = Account("corp.example");
        await Discovery(http, store).DiscoverAsync(account);
        var module = new CustomDomainMailProviderModule(null!, null!, profiles: store);
        var endpoint = module.GetIncomingEndpoint(account);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal(expected, endpoint.SocketOptions);
        Assert.True(account.UseSSL);
    }

    [Fact]
    public async Task Concurrent_accounts_share_one_discovery_per_domain()
    {
        var store = Store();
        var http = new ConfigHttpFactory(_ => Config("corp.example"));
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Discovery(http, store).DiscoverAsync(Account("corp.example"))));
        Assert.Single(http.Requests);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public async Task Failed_discovery_is_cached_briefly_but_does_not_claim_connectivity()
    {
        var store = Store();
        var http = new ConfigHttpFactory(_ => null);
        var account = Account("corp.example");
        await Discovery(http, store).DiscoverAsync(account);
        await Discovery(http, store).DiscoverAsync(Account("corp.example"));
        Assert.Equal(3, http.Requests.Count);
        Assert.Equal("NeedsConfiguration", account.EndpointDiscoveryStatus);
        Assert.Null(store.Find("corp.example")!.IncomingVerifiedAt);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("none")]
    [InlineData("unknown")]
    public async Task Plaintext_or_unknown_encryption_is_never_learned(string socket)
    {
        var store = Store();
        await Discovery(new ConfigHttpFactory(_ => Config("corp.example", socket: socket)), store).DiscoverAsync(Account("corp.example"));
        Assert.Null(store.Find("corp.example")!.Incoming);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("::1")]
    [InlineData("mail.local")]
    [InlineData("mail.internal")]
    [InlineData("https://mail.example")]
    public async Task Invalid_server_hosts_are_not_learned(string host)
    {
        var store = Store();
        await Discovery(new ConfigHttpFactory(_ => Config("corp.example", incoming: host)), store).DiscoverAsync(Account("corp.example"));
        Assert.Null(store.Find("corp.example")!.Incoming);
    }

    [Fact]
    public async Task Config_for_a_different_domain_is_not_applied()
    {
        var store = Store();
        await Discovery(new ConfigHttpFactory(_ => Config("other.example")), store).DiscoverAsync(Account("corp.example"));
        Assert.Null(store.Find("corp.example")!.Incoming);
    }

    [Theory]
    [InlineData("<not-valid-xml")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><clientConfig>&e;</clientConfig>")]
    public async Task Malformed_xml_or_external_entities_do_not_block_the_next_source(string xml)
    {
        var http = new ConfigHttpFactory(u => u.Host == "autoconfig.thunderbird.net" ? Config("corp.example") : xml);
        var account = Account("corp.example");
        await Discovery(http, Store()).DiscoverAsync(account);
        Assert.Equal("imap.provider.example", account.ImapServer);
        Assert.Equal(3, http.Requests.Count);
    }

    [Fact]
    public async Task Oversized_configuration_is_rejected_and_next_source_is_tried()
    {
        var http = new ConfigHttpFactory(u => u.Host == "autoconfig.thunderbird.net" ? Config("corp.example") : new string('x', 65537));
        var account = Account("corp.example");
        await Discovery(http, Store()).DiscoverAsync(account);
        Assert.Equal("imap.provider.example", account.ImapServer);
    }

    [Fact]
    public async Task Cancellation_does_not_cache_a_false_failure()
    {
        var store = Store();
        using var cts = new CancellationTokenSource();
        var http = new ConfigHttpFactory(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Discovery(http, store).DiscoverAsync(Account("corp.example"), cts.Token));
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task Successful_incoming_and_outgoing_methods_are_remembered_independently()
    {
        var store = Store();
        await store.SaveConfigurationAsync(Profile());
        var account = Account("corp.example");
        MailDomainProfileStore.Apply(account, store.Find("corp.example")!);
        account.PreferredIncomingAuth = MailAuthenticationMethod.OAuth2;
        account.PreferredOutgoingAuth = MailAuthenticationMethod.Password;
        await store.RecordAttemptAsync(account, false, true);
        await store.RecordAttemptAsync(account, true, true);
        var restored = Store().Find("corp.example")!;
        Assert.Equal(MailAuthenticationMethod.OAuth2, restored.IncomingAuth);
        Assert.Equal(MailAuthenticationMethod.Password, restored.OutgoingAuth);
        Assert.NotNull(restored.IncomingVerifiedAt);
        Assert.NotNull(restored.OutgoingVerifiedAt);
        var next = Account("corp.example");
        MailDomainProfileStore.Apply(next, restored);
        Assert.Null(next.CredentialLastCheckedAt);
        Assert.Equal(MailCredentialScope.Unknown, next.CredentialScope);
    }

    [Fact]
    public async Task A_bad_account_does_not_erase_working_endpoints_and_smtp_success_does_not_erase_imap_failure()
    {
        var store = Store();
        await store.SaveConfigurationAsync(Profile());
        var account = Account("corp.example");
        MailDomainProfileStore.Apply(account, store.Find("corp.example")!);
        account.PreferredIncomingAuth = MailAuthenticationMethod.Password;
        await store.RecordAttemptAsync(account, false, true);
        await store.RecordAttemptAsync(account, false, false, "AuthenticationRejected");
        await store.RecordAttemptAsync(account, true, true);
        var saved = store.Find("corp.example")!;
        Assert.NotNull(saved.IncomingVerifiedAt);
        Assert.Equal("AuthenticationRejected", saved.IncomingFailure);
        Assert.Null(saved.OutgoingFailure);
        Assert.Equal("imap.provider.example", saved.Incoming!.Host);
    }

    [Fact]
    public async Task Changing_endpoint_invalidates_old_success_and_inflight_old_connection_cannot_verify_it()
    {
        var store = Store();
        await store.SaveConfigurationAsync(Profile());
        var old = Account("corp.example");
        MailDomainProfileStore.Apply(old, store.Find("corp.example")!);
        old.PreferredIncomingAuth = MailAuthenticationMethod.Password;
        await store.RecordAttemptAsync(old, false, true);
        await store.SaveConfigurationAsync(Profile() with { Incoming = new("new.provider.example", 993, true) });
        await store.RecordAttemptAsync(old, false, true);
        Assert.Null(store.Find("corp.example")!.IncomingVerifiedAt);
        Assert.Equal(MailAuthenticationMethod.Unknown, store.Find("corp.example")!.IncomingAuth);
    }

    [Fact]
    public async Task Domain_file_contains_no_account_credentials_and_concurrent_updates_are_not_lost()
    {
        var store = Store();
        await Task.WhenAll(Enumerable.Range(0, 35).Select(i => store.SaveConfigurationAsync(Profile() with { Domain = $"company{i}.example" })));
        Assert.Equal(35, Store().GetAll().Count);
        var text = await File.ReadAllTextAsync(StorePath);
        Assert.DoesNotContain("encrypted-secret", text);
        Assert.DoesNotContain("reader@", text);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Manual_settings_apply_to_existing_and_new_accounts_without_network_discovery()
    {
        var store = Store();
        await store.SaveConfigurationAsync(Profile() with { Incoming = new("imap.manual.example", 1143, false, "%EMAILLOCALPART%") });
        var offline = new ConfigHttpFactory(_ => throw new HttpRequestException("offline"));
        var account = Account("corp.example");
        account.EndpointDiscoveryLastCheckedAt = DateTime.UtcNow;
        account.EndpointDiscoveryStatus = "Fallback";
        await Discovery(offline, store).DiscoverAsync(account);
        Assert.Empty(offline.Requests);
        Assert.Equal("imap.manual.example", account.ImapServer);
        Assert.Equal("reader", account.Username);
        Assert.Equal("Configured", account.EndpointDiscoveryStatus);
    }

    [Theory]
    [InlineData("http://oauth.provider.example/token")]
    [InlineData("https://127.0.0.1/token")]
    [InlineData("https://user:secret@oauth.provider.example/token")]
    [InlineData("not-a-url")]
    public async Task Manual_oauth_endpoint_requires_https_and_public_host(string endpoint)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().SaveConfigurationAsync(Profile() with { OAuth = new("Custom", endpoint, false, false) }));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task Google_hosted_custom_domain_discovers_oauth_without_new_domain_code()
    {
        var store = Store();
        var http = new ConfigHttpFactory(_ => Config("corp.example", incoming: "imap.gmail.com"));
        var account = Account("corp.example");
        account.Password = null;
        account.ClientId = "client-id";
        account.OAuthRefreshToken = "refresh-token";
        await Discovery(http, store).DiscoverAsync(account);
        Assert.Equal("https://oauth2.googleapis.com/token", store.Find("corp.example")!.OAuth!.TokenEndpoint);
        Assert.True(new CustomDomainMailProviderModule(null!, null!, profiles: store).Inspect(account).CanReceive);
    }

    [Fact]
    public async Task Successful_conventional_host_is_learned_after_discovery_has_no_configuration()
    {
        var store = Store();
        var account = Account("corp.example");
        await Discovery(new ConfigHttpFactory(_ => null), store).DiscoverAsync(account);
        account.PreferredIncomingAuth = MailAuthenticationMethod.Password;
        await store.RecordAttemptAsync(account, false, true);
        var learned = Store().Find("corp.example")!;
        Assert.Equal("imap.corp.example", learned.Incoming!.Host);
        Assert.NotNull(learned.IncomingVerifiedAt);
        Assert.Equal("VerifiedFallback", learned.Source);
    }

    private static MailDomainProfile Profile() => new("corp.example", new("imap.provider.example", 993, true), new("smtp.provider.example", 587, false), "Manual");
    private static MailAccount Account(string domain) => new() { EmailAddress = $"reader@{domain}", Name = "reader", Username = $"reader@{domain}", MailProviderKind = MailProviderKind.Custom, Provider = ProviderType.IMAP, ImapServer = $"imap.{domain}", ImapPort = 993, UseSSL = true, Password = "encrypted-secret" };
    private static MailEndpointDiscoveryService Discovery(IHttpClientFactory http, IMailDomainProfileStore store)
        => new(http, NullLogger<MailEndpointDiscoveryService>.Instance, store);
    private static string Config(string domain, string incoming = "imap.provider.example", string outgoing = "smtp.provider.example", string socket = "SSL", int port = 993) => $"""
        <clientConfig><emailProvider id="{domain}"><domain>{domain}</domain>
        <incomingServer type="imap"><hostname>{incoming}</hostname><port>{port}</port><socketType>{socket}</socketType><username>%EMAILADDRESS%</username></incomingServer>
        <outgoingServer type="smtp"><hostname>{outgoing}</hostname><port>587</port><socketType>STARTTLS</socketType></outgoingServer>
        </emailProvider></clientConfig>
        """;

    private sealed class ConfigHttpFactory(Func<Uri, string?> response) : IHttpClientFactory
    {
        public List<Uri> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(this, response));
        private sealed class Handler(ConfigHttpFactory owner, Func<Uri, string?> response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (owner.Requests) owner.Requests.Add(request.RequestUri!);
                var xml = response(request.RequestUri!);
                return Task.FromResult(new HttpResponseMessage(xml is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
                    { Content = new StringContent(xml ?? string.Empty, Encoding.UTF8, "application/xml") });
            }
        }
    }
}
