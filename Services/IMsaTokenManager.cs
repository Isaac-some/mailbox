using MailArchiver.Models;

namespace MailArchiver.Services;

public sealed record MsaAccessToken(string Username, string AccessToken);

public sealed class OutlookGraphAuthorizationException : Exception
{
    public OutlookGraphAuthorizationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public interface IMsaTokenManager
{
    Task<MsaAccessToken> GetAccessTokenAsync(
        MailAccount account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<MsaAccessToken> GetGraphAccessTokenAsync(
        MailAccount account,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}
