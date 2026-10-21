using System.Net;
using System.Net.Sockets;
using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailArchiver.Tests.Services;

public sealed class CustomDomainOAuthConnectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Configured_custom_domain_refreshes_oauth_and_authenticates_real_protocol_client(bool outgoing, bool passwordFallback)
    {
        var root = Path.Combine(Path.GetTempPath(), "custom-oauth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var profiles = new MailDomainProfileStore(Path.Combine(root, "domains.json"));
            var oauth = new ExternalOAuthSettings("New provider", "https://login.new-provider.example/token", true, true);
            await profiles.SaveConfigurationAsync(new("new-provider.example", new("imap.new-provider.example", 993, true),
                new("smtp.new-provider.example", 587, false), "Manual", OAuth: oauth));
            await using var db = new MailArchiverDbContext(new DbContextOptionsBuilder<MailArchiverDbContext>().UseSqlite("Data Source=:memory:").Options);
            await db.Database.OpenConnectionAsync();
            await db.Database.EnsureCreatedAsync();
            var account = new MailAccount { Name = "OAuth test", EmailAddress = "reader@new-provider.example", Username = "reader@new-provider.example",
                MailProviderKind = MailProviderKind.Custom, Provider = ProviderType.IMAP, ClientId = "client-id", ClientSecret = "client-secret",
                OAuthRefreshToken = "original-refresh", OAuthRedirectUri = "oob", Password = passwordFallback ? "bad-password" : null };
            db.MailAccounts.Add(account);
            await db.SaveChangesAsync();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var exchange = ServeAsync(listener, outgoing, timeout.Token);
            var http = new OAuthHttpFactory();
            var tokens = new ExternalOAuthTokenManager(http, db, NullLogger<ExternalOAuthTokenManager>.Instance);
            var module = new CustomDomainMailProviderModule(tokens, new Encryption(), new LoopbackNetwork(port), profiles);
            if (outgoing)
            {
                Assert.True(await module.TestOutgoingConnectionAsync(account, timeout.Token));
                Assert.Equal(MailAuthenticationMethod.OAuth2, account.PreferredOutgoingAuth);
                Assert.NotNull(profiles.Find("new-provider.example")!.OutgoingVerifiedAt);
            }
            else
            {
                using var imap = new ImapClient();
                await imap.ConnectAsync(IPAddress.Loopback.ToString(), port, SecureSocketOptions.None, timeout.Token);
                await module.AuthenticateIncomingAsync(imap, account, timeout.Token);
                await imap.DisconnectAsync(true, timeout.Token);
                Assert.Equal(MailAuthenticationMethod.OAuth2, account.PreferredIncomingAuth);
            }
            var observed = await exchange;
            Assert.Contains("auth=Bearer test-access", observed);
            Assert.Equal("https://login.new-provider.example/token", http.Endpoint);
            Assert.Contains("client_secret=client-secret", http.Body);
            Assert.Contains("redirect_uri=oob", http.Body);
            Assert.Equal("rotated-refresh", (await db.MailAccounts.AsNoTracking().SingleAsync()).OAuthRefreshToken);
            Assert.DoesNotContain("test-access", await File.ReadAllTextAsync(Path.Combine(root, "domains.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<string> ServeAsync(TcpListener listener, bool smtp, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        await writer.WriteLineAsync(smtp ? "220 localhost test" : "* OK test ready");
        string? observed = null;
        while (await reader.ReadLineAsync(token) is { } command)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tag = parts[0];
            if (smtp && command.StartsWith("EHLO"))
            { await writer.WriteLineAsync("250-localhost"); await writer.WriteLineAsync("250 AUTH XOAUTH2"); }
            else if (!smtp && command.Contains(" CAPABILITY"))
            { await writer.WriteLineAsync("* CAPABILITY IMAP4rev1 AUTH=PLAIN AUTH=XOAUTH2"); await writer.WriteLineAsync($"{tag} OK capability"); }
            else if (command.Contains("AUTHENTICATE PLAIN"))
            {
                if (parts.Length == 3) { await writer.WriteLineAsync("+"); await reader.ReadLineAsync(token); }
                await writer.WriteLineAsync($"{tag} NO rejected");
            }
            else if (command.Contains("XOAUTH2"))
            {
                var payload = parts.Last();
                if (payload == "XOAUTH2") { await writer.WriteLineAsync(smtp ? "334" : "+"); payload = await reader.ReadLineAsync(token) ?? ""; }
                observed = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                await writer.WriteLineAsync(smtp ? "235 authenticated" : $"{tag} OK authenticated");
            }
            else if (smtp && command == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
            else if (!smtp && command.Contains(" LOGOUT"))
            { await writer.WriteLineAsync("* BYE"); await writer.WriteLineAsync($"{tag} OK logout"); break; }
            else await writer.WriteLineAsync(smtp ? "500 unsupported" : $"{tag} BAD unsupported");
        }
        return observed ?? throw new InvalidOperationException("Expected OAuth authentication did not occur.");
    }

    private sealed class OAuthHttpFactory : IHttpClientFactory
    {
        public string? Endpoint { get; private set; }
        public string? Body { get; private set; }
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(OAuthHttpFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                owner.Endpoint = request.RequestUri!.ToString();
                owner.Body = await request.Content!.ReadAsStringAsync(token);
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"test-access","expires_in":3600,"refresh_token":"rotated-refresh"}""") };
            }
        }
    }
    // Redirects only synthetic test traffic; production still uses the supplied public TLS endpoint.
    private sealed class LoopbackNetwork(int port) : INetworkMailProxyFactory
    {
        public NetworkRoute Apply(MailKit.IMailService client, string host, int destinationPort)
            => new(NetworkRouteType.Direct, new Uri($"https://{host}:{destinationPort}"), null, 0, false);
        public async Task<NetworkRoute> ConnectAsync(MailKit.IMailService client, string host, int destinationPort, Func<CancellationToken, Task> connect, CancellationToken cancellationToken = default)
        {
            if (client is SmtpClient smtp) await smtp.ConnectAsync(IPAddress.Loopback.ToString(), port, SecureSocketOptions.None, cancellationToken);
            else throw new InvalidOperationException("Unexpected protocol");
            return Apply(client, host, destinationPort);
        }
    }
    private sealed class Encryption : ICredentialEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string encryptedValue) => encryptedValue;
    }
}
