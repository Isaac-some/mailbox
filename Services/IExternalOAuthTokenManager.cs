using MailArchiver.Models;
using MailArchiver.Services.MailProviders;

namespace MailArchiver.Services;

public sealed record ExternalOAuthAccessToken(string Username, string AccessToken);

public interface IExternalOAuthTokenManager
{
    Task<ExternalOAuthAccessToken> GetAccessTokenAsync(
        MailAccount account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<ExternalOAuthAccessToken> GetAccessTokenAsync(
        MailAccount account,
        ExternalOAuthSettings provider,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}
