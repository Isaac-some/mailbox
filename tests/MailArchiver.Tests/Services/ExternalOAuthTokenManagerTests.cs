using System.Net;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailArchiver.Tests.Services;

public class ExternalOAuthTokenManagerTests
{
    [Fact]
    public async Task Gmail_refresh_posts_to_google_and_persists_access_token()
    {
        var handler = new CapturingHandler("""{"access_token":"google-access","expires_in":3600,"scope":"https://mail.google.com/"}""");
        await using var fixture = await TokenFixture.CreateAsync("reader@gmail.com", clientSecret: null, handler);

        var token = await fixture.Manager.GetAccessTokenAsync(fixture.Account);

        Assert.Equal("reader@gmail.com", token.Username);
        Assert.Equal("google-access", token.AccessToken);
        Assert.Equal("https://oauth2.googleapis.com/token", handler.RequestUri?.ToString());
        Assert.Contains("client_id=google-client", handler.FormBody);
        Assert.Contains("refresh_token=old-refresh", handler.FormBody);
        Assert.DoesNotContain("client_secret=", handler.FormBody);
        Assert.Equal("google-access", fixture.Account.OAuthAccessToken);
    }

    [Fact]
    public async Task Yahoo_refresh_sends_secret_and_persists_rotated_refresh_token()
    {
        var handler = new CapturingHandler("""{"access_token":"yahoo-access","expires_in":3600,"refresh_token":"new-refresh"}""");
        await using var fixture = await TokenFixture.CreateAsync("reader@yahoo.com", "yahoo-secret", handler);

        await fixture.Manager.GetAccessTokenAsync(fixture.Account);

        Assert.Equal("https://api.login.yahoo.com/oauth2/get_token", handler.RequestUri?.ToString());
        Assert.Contains("client_secret=yahoo-secret", handler.FormBody);
        Assert.Contains("redirect_uri=oob", handler.FormBody);
        Assert.Equal("new-refresh", fixture.Account.OAuthRefreshToken);
        var persisted = await fixture.Context.MailAccounts.AsNoTracking().SingleAsync();
        Assert.Equal("new-refresh", persisted.OAuthRefreshToken);
    }

    private sealed class TokenFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TokenFixture(
            SqliteConnection connection,
            MailArchiverDbContext context,
            MailAccount account,
            ExternalOAuthTokenManager manager)
        {
            _connection = connection;
            Context = context;
            Account = account;
            Manager = manager;
        }

        public MailArchiverDbContext Context { get; }
        public MailAccount Account { get; }
        public ExternalOAuthTokenManager Manager { get; }

        public static async Task<TokenFixture> CreateAsync(
            string email,
            string? clientSecret,
            HttpMessageHandler handler)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MailArchiverDbContext>().UseSqlite(connection).Options;
            var context = new MailArchiverDbContext(options);
            await context.Database.EnsureCreatedAsync();
            var account = new MailAccount
            {
                Name = email,
                EmailAddress = email,
                Username = email,
                Provider = ProviderType.IMAP,
                ClientId = "google-client",
                ClientSecret = clientSecret,
                OAuthRedirectUri = email.Contains("yahoo", StringComparison.OrdinalIgnoreCase) ? "oob" : null,
                OAuthRefreshToken = "old-refresh",
                OAuthAccessToken = "expired",
                OAuthTokenExpiry = DateTime.UtcNow.AddMinutes(-1),
                IsEnabled = true
            };
            context.MailAccounts.Add(account);
            await context.SaveChangesAsync();
            var client = new HttpClient(handler);
            var manager = new ExternalOAuthTokenManager(
                new SingleClientFactory(client), context, NullLogger<ExternalOAuthTokenManager>.Instance);
            return new TokenFixture(connection, context, account, manager);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string FormBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            FormBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            };
        }
    }
}
