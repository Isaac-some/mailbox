using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services;

public sealed class MsaTokenManager : IMsaTokenManager
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> AccountLocks = new();

    private readonly IMsaOAuthService _oauth;
    private readonly IMsaGraphTokenCache _graphTokenCache;
    private readonly MailArchiverDbContext _dbContext;
    private readonly ILogger<MsaTokenManager> _logger;

    public MsaTokenManager(
        IMsaOAuthService oauth,
        IMsaGraphTokenCache graphTokenCache,
        MailArchiverDbContext dbContext,
        ILogger<MsaTokenManager> logger)
    {
        _oauth = oauth;
        _graphTokenCache = graphTokenCache;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<MsaAccessToken> GetGraphAccessTokenAsync(
        MailAccount account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);

        if (!forceRefresh
            && _graphTokenCache.TryGet(account.Id, out var cached)
            && cached.RefreshTokenFingerprint == Fingerprint(account.OAuthRefreshToken!)
            && cached.ExpiryUtc > DateTime.UtcNow.AddMinutes(1))
        {
            return new MsaAccessToken(account.Username ?? account.EmailAddress, cached.AccessToken);
        }

        var accountLock = AccountLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh
                && _graphTokenCache.TryGet(account.Id, out cached)
                && cached.RefreshTokenFingerprint == Fingerprint(account.OAuthRefreshToken!)
                && cached.ExpiryUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return new MsaAccessToken(account.Username ?? account.EmailAddress, cached.AccessToken);
            }

            var tracked = await _dbContext.MailAccounts
                .FirstOrDefaultAsync(candidate => candidate.Id == account.Id, cancellationToken)
                ?? account;

            MsaTokenResult refreshed;
            try
            {
                refreshed = await _oauth.RefreshGraphAccessTokenAsync(
                    tracked.OAuthRefreshToken!, tracked.ClientId, tracked.ClientSecret);
            }
            catch (MsaOAuthTokenException ex) when (ex.IsAuthorizationFailure)
            {
                _graphTokenCache.Remove(account.Id);
                throw new OutlookGraphAuthorizationException(
                    "Outlook Graph 授权失败，请确认 Refresh Token 包含 Mail.Send 权限。", ex);
            }

            if (!MsaOAuthScopePolicy.CanSend(refreshed.GrantedScopes))
            {
                _graphTokenCache.Remove(account.Id);
                throw new OutlookGraphAuthorizationException(
                    "Outlook Graph 授权缺少 Mail.Send 权限，请重新导入正确的 Refresh Token。");
            }

            if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            {
                tracked.OAuthRefreshToken = refreshed.RefreshToken;
                account.OAuthRefreshToken = refreshed.RefreshToken;
                if (_dbContext.Entry(tracked).State != EntityState.Detached)
                    await _dbContext.SaveChangesAsync(cancellationToken);
            }

            var activeRefreshToken = tracked.OAuthRefreshToken!;
            var graphToken = new CachedMsaGraphToken(
                refreshed.AccessToken,
                refreshed.Expiry,
                Fingerprint(activeRefreshToken));
            _graphTokenCache.Set(account.Id, graphToken);
            return new MsaAccessToken(
                tracked.Username ?? tracked.EmailAddress,
                graphToken.AccessToken);
        }
        finally
        {
            accountLock.Release();
        }
    }

    public async Task<MsaAccessToken> GetSmtpAccessTokenAsync(
        MailAccount account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        var accountLock = AccountLocks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await accountLock.WaitAsync(cancellationToken);
        try
        {
            var tracked = await _dbContext.MailAccounts
                .FirstOrDefaultAsync(candidate => candidate.Id == account.Id, cancellationToken)
                ?? account;
            var refreshed = await _oauth.RefreshSmtpAccessTokenAsync(
                tracked.OAuthRefreshToken!, tracked.ClientId, tracked.ClientSecret);

            if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
            {
                tracked.OAuthRefreshToken = refreshed.RefreshToken;
                account.OAuthRefreshToken = refreshed.RefreshToken;
                if (_dbContext.Entry(tracked).State != EntityState.Detached)
                    await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return new MsaAccessToken(
                tracked.Username ?? tracked.EmailAddress,
                refreshed.AccessToken);
        }
        finally
        {
            accountLock.Release();
        }
    }

    public async Task<MsaAccessToken> GetAccessTokenAsync(
        MailAccount account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);

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
                _logger.LogInformation("Refreshing Microsoft OAuth token for account {AccountId}", tracked.Id);
                var refreshed = await _oauth.RefreshAccessTokenAsync(
                    tracked.OAuthRefreshToken!, tracked.ClientId, tracked.ClientSecret, cancellationToken);

                tracked.OAuthAccessToken = refreshed.AccessToken;
                tracked.OAuthTokenExpiry = refreshed.Expiry;
                if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
                    tracked.OAuthRefreshToken = refreshed.RefreshToken;
                if (!string.IsNullOrWhiteSpace(refreshed.GrantedScopes))
                    tracked.OAuthGrantedScopes = refreshed.GrantedScopes;
                if (!string.IsNullOrWhiteSpace(refreshed.AuthorizedUsername))
                    tracked.Username = refreshed.AuthorizedUsername;

                if (_dbContext.Entry(tracked).State != EntityState.Detached)
                    await _dbContext.SaveChangesAsync(cancellationToken);

                CopyTokenFields(tracked, account);
            }

            return new MsaAccessToken(
                tracked.Username ?? tracked.EmailAddress,
                tracked.OAuthAccessToken
                    ?? throw new InvalidOperationException("Outlook 授权没有返回可用的访问令牌。"));
        }
        finally
        {
            accountLock.Release();
        }
    }

    private static void ValidateAccount(MailAccount account)
    {
        if (account.Provider != ProviderType.MSA)
            throw new InvalidOperationException("Only Outlook personal accounts use Microsoft OAuth tokens.");
        if (string.IsNullOrWhiteSpace(account.OAuthRefreshToken))
            throw new InvalidOperationException("Outlook 账号尚未完成授权，请先重新授权。");
    }

    private static string Fingerprint(string refreshToken)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    private static void CopyTokenFields(MailAccount source, MailAccount destination)
    {
        if (ReferenceEquals(source, destination))
            return;

        destination.OAuthAccessToken = source.OAuthAccessToken;
        destination.OAuthRefreshToken = source.OAuthRefreshToken;
        destination.OAuthTokenExpiry = source.OAuthTokenExpiry;
        destination.OAuthGrantedScopes = source.OAuthGrantedScopes;
        destination.Username = source.Username;
    }
}
