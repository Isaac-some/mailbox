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
    public async Task Imap_refresh_explicitly_requests_only_IMAP_and_offline_access()
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
        var scopes = handler.Form["scope"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(MsaOAuthScopePolicy.Imap, scopes);
        Assert.Contains("offline_access", scopes);
        Assert.DoesNotContain(MsaOAuthScopePolicy.GraphMailSend, scopes);
    }

    [Fact]
    public async Task Graph_refresh_requests_only_Graph_Mail_Send_and_offline_access()
    {
        var handler = new CapturingHandler(MsaOAuthScopePolicy.GraphMailSend);
        var service = new MsaOAuthService(
            new SingleClientFactory(new HttpClient(handler)),
            NullLogger<MsaOAuthService>.Instance,
            Options.Create(new MsaOAuthOptions
            {
                DefaultClientId = "default-client",
                Authority = "https://login.example.test/oauth2/v2.0"
            }));

        var result = await service.RefreshGraphAccessTokenAsync("vendor-refresh", "vendor-client", null);

        Assert.Equal(MsaOAuthScopePolicy.GraphMailSend, result.GrantedScopes);
        var scopes = handler.Form["scope"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(MsaOAuthScopePolicy.GraphMailSend, scopes);
        Assert.Contains("offline_access", scopes);
        Assert.DoesNotContain(MsaOAuthScopePolicy.Imap, scopes);
        Assert.DoesNotContain("https://outlook.office.com/SMTP.Send", scopes);
    }

    [Fact]
    public async Task Smtp_refresh_requests_only_SMTP_Send_and_offline_access()
    {
        var handler = new CapturingHandler(MsaOAuthScopePolicy.SmtpSend);
        var service = new MsaOAuthService(
            new SingleClientFactory(new HttpClient(handler)),
            NullLogger<MsaOAuthService>.Instance,
            Options.Create(new MsaOAuthOptions
            {
                DefaultClientId = "default-client",
                Authority = "https://login.example.test/oauth2/v2.0"
            }));

        var result = await service.RefreshSmtpAccessTokenAsync("vendor-refresh", "vendor-client", null);

        Assert.Equal(MsaOAuthScopePolicy.SmtpSend, result.GrantedScopes);
        var scopes = handler.Form["scope"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(MsaOAuthScopePolicy.SmtpSend, scopes);
        Assert.Contains("offline_access", scopes);
        Assert.DoesNotContain(MsaOAuthScopePolicy.Imap, scopes);
        Assert.DoesNotContain(MsaOAuthScopePolicy.GraphMailSend, scopes);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler(string grantedScope = MsaOAuthScopePolicy.Imap) : HttpMessageHandler
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
                    grantedScope + "\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
