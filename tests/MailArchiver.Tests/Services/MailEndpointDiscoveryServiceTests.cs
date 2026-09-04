using System.Net;
using System.Net.Http;
using System.Text;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailArchiver.Tests.Services;

public class MailEndpointDiscoveryServiceTests
{
    [Fact]
    public async Task Discovers_both_endpoints_from_mozilla_autoconfig_without_touching_credentials()
    {
        const string xml = """
            <?xml version="1.0"?>
            <clientConfig version="1.1">
              <emailProvider id="corp.example">
                <incomingServer type="imap">
                  <hostname>mail.corp.example</hostname>
                  <port>993</port>
                  <socketType>SSL</socketType>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>relay.provider.example</hostname>
                  <port>465</port>
                  <socketType>SSL</socketType>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;
        var handler = new StaticResponseHandler(xml);
        var service = new MailEndpointDiscoveryService(
            new SingleClientFactory(new HttpClient(handler)),
            NullLogger<MailEndpointDiscoveryService>.Instance);
        var account = new MailAccount
        {
            EmailAddress = "reader@corp.example",
            MailProviderKind = MailProviderKind.Custom,
            ImapServer = "imap.corp.example",
            ImapPort = 993,
            UseSSL = true,
            Password = "encrypted-value"
        };

        await service.DiscoverAsync(account);

        Assert.Equal("mail.corp.example", account.ImapServer);
        Assert.Equal(993, account.ImapPort);
        Assert.True(account.UseSSL);
        Assert.Equal("relay.provider.example", account.SmtpServer);
        Assert.Equal(465, account.SmtpPort);
        Assert.True(account.SmtpUseSSL);
        Assert.Equal("Discovered", account.EndpointDiscoveryStatus);
        Assert.NotNull(account.EndpointDiscoveryLastCheckedAt);
        Assert.Equal("encrypted-value", account.Password);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string _xml;

        public StaticResponseHandler(string xml) => _xml = xml;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_xml, Encoding.UTF8, "application/xml")
            });
        }
    }
}
