namespace MailArchiver.Services;

public sealed record PlatformSession(
    string Username,
    bool IsAdmin,
    string? BearerToken,
    string? CookieHeader,
    DateTimeOffset AuthenticatedAt);

public interface IPlatformSessionStore
{
    PlatformSession? Current { get; }
    void Set(PlatformSession session);
    void Clear();
}

public sealed class PlatformSessionStore : IPlatformSessionStore
{
    private readonly object _gate = new();
    private PlatformSession? _current;

    public PlatformSession? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Set(PlatformSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate) _current = session;
    }

    public void Clear()
    {
        lock (_gate) _current = null;
    }
}
