using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MailArchiver.Tests.Services;

public class MsaTokenManagerTests
{
    [Fact]
    public async Task Refresh_persists_rotated_token_identity_and_granted_scopes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailArchiverDbContext>().UseSqlite(connection).Options;
        await using var context = new MailArchiverDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var account = new MailAccount
        {
            Name = "Outlook",
            EmailAddress = "alias@outlook.com",
            Username = "alias@outlook.com",
            Provider = ProviderType.MSA,
            OAuthRefreshToken = "old-refresh",
            OAuthAccessToken = "expired",
            OAuthTokenExpiry = DateTime.UtcNow.AddMinutes(-1),
            IsEnabled = true
        };
        context.MailAccounts.Add(account);
        await context.SaveChangesAsync();

        var manager = new MsaTokenManager(
            new FakeOAuthService(),
            new MsaGraphTokenCache(),
            context,
            NullLogger<MsaTokenManager>.Instance);
        var token = await manager.GetAccessTokenAsync(account);

        Assert.Equal("primary@outlook.com", token.Username);
        Assert.Equal("new-access", token.AccessToken);
        var persisted = await context.MailAccounts.AsNoTracking().SingleAsync();
        Assert.Equal("new-refresh", persisted.OAuthRefreshToken);
        Assert.Equal(MsaOAuthScopePolicy.Imap, persisted.OAuthGrantedScopes);
        Assert.Equal("primary@outlook.com", persisted.Username);
    }

    [Fact]
    public async Task Graph_refresh_is_cached_and_does_not_overwrite_IMAP_access_token_or_scopes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailArchiverDbContext>().UseSqlite(connection).Options;
        await using var context = new MailArchiverDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var account = new MailAccount
        {
            Name = "Outlook",
            EmailAddress = "sender@outlook.com",
            Username = "sender@outlook.com",
            Provider = ProviderType.MSA,
            OAuthRefreshToken = "old-refresh",
            OAuthAccessToken = "imap-access",
            OAuthTokenExpiry = DateTime.UtcNow.AddHours(1),
            OAuthGrantedScopes = MsaOAuthScopePolicy.Imap,
            IsEnabled = true
        };
        context.MailAccounts.Add(account);
        await context.SaveChangesAsync();
        var oauth = new FakeOAuthService();
        var manager = new MsaTokenManager(
            oauth,
            new MsaGraphTokenCache(),
            context,
            NullLogger<MsaTokenManager>.Instance);

        var first = await manager.GetGraphAccessTokenAsync(account);
        var second = await manager.GetGraphAccessTokenAsync(account);

        Assert.Equal("graph-access", first.AccessToken);
        Assert.Equal("graph-access", second.AccessToken);
        Assert.Equal(1, oauth.GraphRefreshCount);
        var persisted = await context.MailAccounts.AsNoTracking().SingleAsync();
        Assert.Equal("imap-access", persisted.OAuthAccessToken);
        Assert.Equal(MsaOAuthScopePolicy.Imap, persisted.OAuthGrantedScopes);
        Assert.Equal("graph-refresh", persisted.OAuthRefreshToken);
    }

    [Fact]
    public async Task Graph_cache_is_invalidated_when_the_accounts_refresh_token_changes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailArchiverDbContext>().UseSqlite(connection).Options;
        await using var context = new MailArchiverDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var account = new MailAccount
        {
            Name = "Outlook",
            EmailAddress = "sender@outlook.com",
            Username = "sender@outlook.com",
            Provider = ProviderType.MSA,
            OAuthRefreshToken = "first-refresh",
            IsEnabled = true
        };
        context.MailAccounts.Add(account);
        await context.SaveChangesAsync();
        var oauth = new FakeOAuthService();
        var manager = new MsaTokenManager(
            oauth,
            new MsaGraphTokenCache(),
            context,
            NullLogger<MsaTokenManager>.Instance);

        await manager.GetGraphAccessTokenAsync(account);
        account.OAuthRefreshToken = "replacement-refresh";
        await context.SaveChangesAsync();
        await manager.GetGraphAccessTokenAsync(account);

        Assert.Equal(2, oauth.GraphRefreshCount);
    }

    [Fact]
    public async Task Read_token_is_cached_separately_and_invalidated_by_imported_credentials()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MailArchiverDbContext(new DbContextOptionsBuilder<MailArchiverDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var account = new MailAccount { Name = "Read", EmailAddress = "read@outlook.com", Provider = ProviderType.MSA,
            ClientId = "client", OAuthRefreshToken = "refresh", OAuthAccessToken = "imap-access", OAuthGrantedScopes = MsaOAuthScopePolicy.Imap };
        db.Add(account); await db.SaveChangesAsync();
        var oauth = new FakeOAuthService();
        var manager = new MsaTokenManager(oauth, new MsaGraphTokenCache(), db, NullLogger<MsaTokenManager>.Instance, new());
        Assert.Equal("read-access", (await manager.GetGraphReadAccessTokenAsync(account)).AccessToken);
        Assert.Equal("read-access", (await manager.GetGraphReadAccessTokenAsync(account)).AccessToken);
        Assert.Equal(1, oauth.ReadRefreshCount);
        Assert.Equal("read-refresh", account.OAuthRefreshToken);
        Assert.Equal("imap-access", account.OAuthAccessToken);
        Assert.Equal(MsaOAuthScopePolicy.Imap, account.OAuthGrantedScopes);
        Assert.Equal("graph-access", (await manager.GetGraphAccessTokenAsync(account)).AccessToken);
        Assert.Equal(1, oauth.GraphRefreshCount);
        await manager.GetGraphReadAccessTokenAsync(account);
        Assert.Equal(2, oauth.ReadRefreshCount); // sending rotated the shared refresh token
        account.ClientId = "new-client"; await db.SaveChangesAsync();
        await manager.GetGraphReadAccessTokenAsync(account); Assert.Equal(3, oauth.ReadRefreshCount);
        account.OAuthRefreshToken = "imported-refresh"; await db.SaveChangesAsync();
        await manager.GetGraphReadAccessTokenAsync(account); Assert.Equal(4, oauth.ReadRefreshCount);
        Assert.Equal("imap-access", (await db.MailAccounts.AsNoTracking().SingleAsync()).OAuthAccessToken);
    }

    private sealed class FakeOAuthService : IMsaOAuthService
    {
        public int ReadRefreshCount { get; private set; }
        public Task<MsaTokenResult> RefreshGraphReadAccessTokenAsync(string refreshToken, string? clientId, string? clientSecret, CancellationToken cancellationToken = default)
        {
            ReadRefreshCount++;
            return Task.FromResult(new MsaTokenResult { AccessToken = "read-access", RefreshToken = "read-refresh",
                Expiry = DateTime.UtcNow.AddHours(1), GrantedScopes = "Mail.ReadWrite" });
        }
        public int GraphRefreshCount { get; private set; }
        public Task<DeviceCodeResult> StartDeviceCodeAsync(string? clientId) => throw new NotSupportedException();
        public Task<MsaPollResult> PollDeviceCodeAsync(string? clientId, string deviceCode, int currentInterval) => throw new NotSupportedException();
        public string? GetDefaultClientId() => "client";

        public Task<MsaTokenResult> RefreshAccessTokenAsync(string refreshToken, string? clientId, string? clientSecret, CancellationToken cancellationToken = default)
            => Task.FromResult(new MsaTokenResult
            {
                AccessToken = "new-access",
                RefreshToken = "new-refresh",
                Expiry = DateTime.UtcNow.AddHours(1),
                AuthorizedUsername = "primary@outlook.com",
                GrantedScopes = MsaOAuthScopePolicy.Imap
            });

        public Task<MsaTokenResult> RefreshGraphAccessTokenAsync(
            string refreshToken,
            string? clientId,
            string? clientSecret)
        {
            GraphRefreshCount++;
            return Task.FromResult(new MsaTokenResult
            {
                AccessToken = "graph-access",
                RefreshToken = "graph-refresh",
                Expiry = DateTime.UtcNow.AddHours(1),
                GrantedScopes = MsaOAuthScopePolicy.GraphMailSend
            });
        }

        public Task<MsaTokenResult> RefreshSmtpAccessTokenAsync(
            string refreshToken,
            string? clientId,
            string? clientSecret)
            => Task.FromResult(new MsaTokenResult
            {
                AccessToken = "smtp-access",
                RefreshToken = "smtp-refresh",
                Expiry = DateTime.UtcNow.AddHours(1),
                GrantedScopes = MsaOAuthScopePolicy.SmtpSend
            });
    }
}
