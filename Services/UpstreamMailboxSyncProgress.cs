using System.Collections.Concurrent;

namespace MailArchiver.Services;

public sealed record UpstreamMailboxSyncSnapshot(
    bool Started = false, bool Running = false, int Processed = 0,
    int Total = 0, int Rejected = 0, string? Error = null);

/// <summary>Per-user progress only; never stores addresses or credentials.</summary>
public sealed class UpstreamMailboxSyncProgress
{
    private readonly ConcurrentDictionary<int, UpstreamMailboxSyncSnapshot> _users = new();
    public UpstreamMailboxSyncSnapshot Get(int userId) => _users.GetValueOrDefault(userId) ?? new();
    public void Begin(int userId) => _users[userId] = new(Started: true, Running: true);
    public void Advance(int userId, int processed, int total, int rejected) =>
        _users.AddOrUpdate(userId, new UpstreamMailboxSyncSnapshot(true, true, processed, total, rejected),
            (_, old) => old with { Processed = Math.Max(old.Processed, processed), Total = total, Rejected = rejected });
    public void Complete(int userId, string? error) =>
        _users.AddOrUpdate(userId, new UpstreamMailboxSyncSnapshot(Started: true, Error: error),
            (_, old) => old with { Running = false, Error = error });
}
