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

        var manager = new MsaTokenManager(new FakeOAuthService(), context, NullLogger<MsaTokenManager>.Instance);
        var token = await manager.GetAccessTokenAsync(account);

        Assert.Equal("primary@outlook.com", token.Username);
        Assert.Equal("new-access", token.AccessToken);
        var persisted = await context.MailAccounts.AsNoTracking().SingleAsync();
        Assert.Equal("new-refresh", persisted.OAuthRefreshToken);
        Assert.Equal(MsaOAuthScopePolicy.Smtp, persisted.OAuthGrantedScopes);
        Assert.Equal("primary@outlook.com", persisted.Username);
    }

    private sealed class FakeOAuthService : IMsaOAuthService
    {
        public Task<DeviceCodeResult> StartDeviceCodeAsync(string? clientId) => throw new NotSupportedException();
        public Task<MsaPollResult> PollDeviceCodeAsync(string? clientId, string deviceCode, int currentInterval) => throw new NotSupportedException();
        public string? GetDefaultClientId() => "client";

        public Task<MsaTokenResult> RefreshAccessTokenAsync(string refreshToken, string? clientId, string? clientSecret)
            => Task.FromResult(new MsaTokenResult
            {
                AccessToken = "new-access",
                RefreshToken = "new-refresh",
                Expiry = DateTime.UtcNow.AddHours(1),
                AuthorizedUsername = "primary@outlook.com",
                GrantedScopes = MsaOAuthScopePolicy.Smtp
            });
    }
}
