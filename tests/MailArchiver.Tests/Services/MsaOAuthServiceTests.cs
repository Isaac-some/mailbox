using System.Net;
using System.Text;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Services;

public class MsaOAuthServiceTests
{
    [Fact]
    public async Task Refresh_does_not_request_new_scopes_from_a_vendor_refresh_token()
    {
        var handler = new CapturingHandler();
        var service = new MsaOAuthService(
            new SingleClientFactory(new HttpClient(handler)),
            NullLogger<MsaOAuthService>.Instance,
            Options.Create(new MsaOAuthOptions
            {
                DefaultClientId = "default-client",
                Authority = "https://login.example.test/oauth2/v2.0"
            }));

        var result = await service.RefreshAccessTokenAsync("vendor-refresh", "vendor-client", null);

        Assert.Equal("new-access", result.AccessToken);
        Assert.Equal(MsaOAuthScopePolicy.Imap, result.GrantedScopes);
        Assert.Equal("refresh_token", handler.Form["grant_type"]);
        Assert.Equal("vendor-client", handler.Form["client_id"]);
        Assert.Equal("vendor-refresh", handler.Form["refresh_token"]);
        Assert.False(handler.Form.ContainsKey("scope"));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Form { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Form = body.Split('&')
                .Select(part => part.Split('=', 2))
                .ToDictionary(
                    part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
                    part => Uri.UnescapeDataString(part[1].Replace('+', ' ')));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600,\"scope\":\"" +
                    MsaOAuthScopePolicy.Imap + "\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
