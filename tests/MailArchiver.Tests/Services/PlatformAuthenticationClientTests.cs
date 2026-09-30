using System.Net;
using System.Text;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Services;

public sealed class PlatformAuthenticationClientTests
{
    [Fact]
    public async Task EnsureAuthenticatedAsync_logs_in_once_and_stores_bearer_token()
    {
        var handler = new LoginHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":{\"accessToken\":\"platform-token\",\"isAdmin\":false}}",
                    Encoding.UTF8,
                    "application/json")
            });
        var sessions = new PlatformSessionStore();
        var client = CreateClient(handler, sessions, "platform-user", "platform-password");

        var first = await client.EnsureAuthenticatedAsync();
        var second = await client.EnsureAuthenticatedAsync();

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("platform-user", handler.Username);
        Assert.Equal("platform-password", handler.Password);
        Assert.Equal("platform-token", sessions.Current?.BearerToken);
    }

    [Fact]
    public async Task EnsureAuthenticatedAsync_accepts_cookie_only_login_response()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain")
        };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc; Path=/; HttpOnly");
        var handler = new LoginHandler(response);
        var sessions = new PlatformSessionStore();
        var client = CreateClient(handler, sessions, "platform-user", "platform-password");

        var result = await client.EnsureAuthenticatedAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("session=abc", sessions.Current?.CookieHeader);
        Assert.Null(sessions.Current?.BearerToken);
    }

    [Fact]
    public async Task EnsureAuthenticatedAsync_fails_without_fixed_credentials_before_request()
    {
        var handler = new LoginHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler, new PlatformSessionStore(), string.Empty, string.Empty);

        var result = await client.EnsureAuthenticatedAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("固定账号", result.Error);
        Assert.Equal(0, handler.RequestCount);
    }

    private static PlatformAuthenticationClient CreateClient(
        HttpMessageHandler handler,
        IPlatformSessionStore sessions,
        string username,
        string password)
        => new(
            new SingleClientFactory(new HttpClient(handler)),
            Options.Create(new PlatformAuthenticationOptions
            {
                BaseUrl = "https://platform.example",
                LoginPath = "/api/auth/login",
                Username = username,
                Password = password
            }),
            sessions,
            NullLogger<PlatformAuthenticationClient>.Instance);

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class LoginHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? Username { get; private set; }
        public string? Password { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            Username = document.RootElement.GetProperty("username").GetString();
            Password = document.RootElement.GetProperty("password").GetString();
            return response;
        }
    }
}
