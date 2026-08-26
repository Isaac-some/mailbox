using MailArchiver.Models;

namespace MailArchiver.Services;

public sealed record MsaAccessToken(string Username, string AccessToken);

public interface IMsaTokenManager
{
    Task<MsaAccessToken> GetAccessTokenAsync(
        MailAccount account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}
