using System.Collections.Concurrent;
using System.Text.Json;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.MailProviders;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services;

public sealed class ExternalOAuthTokenManager : IExternalOAuthTokenManager
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> AccountLocks = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MailArchiverDbContext _dbContext;
    private readonly ILogger<ExternalOAuthTokenManager> _logger;

    public ExternalOAuthTokenManager(
        IHttpClientFactory httpClientFactory,
        MailArchiverDbContext dbContext,
        ILogger<ExternalOAuthTokenManager> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ExternalOAuthAccessToken> GetAccessTokenAsync(
        MailAccount account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (account.Provider != ProviderType.IMAP ||
            !ExternalOAuthProviderPolicy.TryResolve(account.EmailAddress, out var provider))
            throw new InvalidOperationException("只有 Gmail 和 Yahoo 账号支持此 OAuth 令牌流程。");
        return await GetAccessTokenAsync(account, new ExternalOAuthSettings(
            provider.Provider,
            provider.TokenEndpoint,
            provider.RequiresClientSecret,
            provider.RequiresRedirectUri), forceRefresh, cancellationToken);
    }

    public async Task<ExternalOAuthAccessToken> GetAccessTokenAsync(
        MailAccount account,
        ExternalOAuthSettings provider,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!HasUsableCredentials(account, provider))
            throw new InvalidOperationException(
                provider.RequiresClientSecret
                    ? $"{provider.ProviderName} OAuth 缺少 Client ID、Client Secret、Refresh Token 或 Redirect URI。"
                    : $"{provider.ProviderName} OAuth 缺少 Client ID 或 Refresh Token。");

        var accountLock = AccountLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(cancellationToken);
        try
        {
            var tracked = await _dbContext.MailAccounts
                .FirstOrDefaultAsync(candidate => candidate.Id == account.Id, cancellationToken)
                ?? account;
            var needsRefresh = forceRefresh
                || string.IsNullOrWhiteSpace(tracked.OAuthAccessToken)
                || tracked.OAuthTokenExpiry is null
                || tracked.OAuthTokenExpiry <= DateTime.UtcNow.AddMinutes(1);

            if (needsRefresh)
            {
                _logger.LogInformation("Refreshing {Provider} OAuth token for account {AccountId}",
                    provider.ProviderName, tracked.Id);
                var body = new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = tracked.ClientId!,
                    ["refresh_token"] = tracked.OAuthRefreshToken!
                };
                if (!string.IsNullOrWhiteSpace(tracked.ClientSecret))
                    body["client_secret"] = tracked.ClientSecret;
                if (!string.IsNullOrWhiteSpace(tracked.OAuthRedirectUri))
                    body["redirect_uri"] = tracked.OAuthRedirectUri;

                var client = _httpClientFactory.CreateClient("ExternalMailOAuth");
                using var response = await client.PostAsync(
                    provider.TokenEndpoint,
                    new FormUrlEncodedContent(body),
                    cancellationToken);
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("{Provider} OAuth refresh failed for account {AccountId}: HTTP {Status}",
                        provider.ProviderName, tracked.Id, (int)response.StatusCode);
                    throw new InvalidOperationException(
                        $"{provider.ProviderName} OAuth 刷新失败（HTTP {(int)response.StatusCode}），请检查 Client ID、Client Secret 和 Refresh Token。");
                }

                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                tracked.OAuthAccessToken = root.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException($"{provider.ProviderName} 没有返回 access token。");
                var expiresIn = root.TryGetProperty("expires_in", out var expiry)
                    ? expiry.GetInt32()
                    : 3600;
                tracked.OAuthTokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
                if (root.TryGetProperty("refresh_token", out var rotatedRefreshToken) &&
                    !string.IsNullOrWhiteSpace(rotatedRefreshToken.GetString()))
                    tracked.OAuthRefreshToken = rotatedRefreshToken.GetString();
                if (root.TryGetProperty("scope", out var scopes) && !string.IsNullOrWhiteSpace(scopes.GetString()))
                    tracked.OAuthGrantedScopes = scopes.GetString();

                if (_dbContext.Entry(tracked).State != EntityState.Detached)
                    await _dbContext.SaveChangesAsync(cancellationToken);
                CopyTokenFields(tracked, account);
            }

            return new ExternalOAuthAccessToken(
                tracked.Username ?? tracked.EmailAddress,
                tracked.OAuthAccessToken
                    ?? throw new InvalidOperationException($"{provider.ProviderName} 没有可用的 access token。"));
        }
        finally
        {
            accountLock.Release();
        }
    }

    public static bool HasUsableCredentials(MailAccount account, ExternalOAuthSettings provider)
        => !string.IsNullOrWhiteSpace(account.ClientId)
            && !string.IsNullOrWhiteSpace(account.OAuthRefreshToken)
            && (!provider.RequiresClientSecret || !string.IsNullOrWhiteSpace(account.ClientSecret))
            && (!provider.RequiresRedirectUri || !string.IsNullOrWhiteSpace(account.OAuthRedirectUri));

    private static void CopyTokenFields(MailAccount source, MailAccount destination)
    {
        if (ReferenceEquals(source, destination))
            return;

        destination.OAuthAccessToken = source.OAuthAccessToken;
        destination.OAuthRefreshToken = source.OAuthRefreshToken;
        destination.OAuthTokenExpiry = source.OAuthTokenExpiry;
        destination.OAuthGrantedScopes = source.OAuthGrantedScopes;
    }
}
