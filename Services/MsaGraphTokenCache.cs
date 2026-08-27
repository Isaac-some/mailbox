using System.Collections.Concurrent;

namespace MailArchiver.Services;

public sealed record CachedMsaGraphToken(
    string AccessToken,
    DateTime ExpiryUtc,
    string RefreshTokenFingerprint);

public interface IMsaGraphTokenCache
{
    bool TryGet(int accountId, out CachedMsaGraphToken token);
    void Set(int accountId, CachedMsaGraphToken token);
    void Remove(int accountId);
}

public sealed class MsaGraphTokenCache : IMsaGraphTokenCache
{
    private readonly ConcurrentDictionary<int, CachedMsaGraphToken> _tokens = new();

    public bool TryGet(int accountId, out CachedMsaGraphToken token)
        => _tokens.TryGetValue(accountId, out token!);

    public void Set(int accountId, CachedMsaGraphToken token)
        => _tokens[accountId] = token;

    public void Remove(int accountId)
        => _tokens.TryRemove(accountId, out _);
}
