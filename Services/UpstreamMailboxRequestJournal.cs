namespace MailArchiver.Services;

public sealed record UpstreamMailboxRequestReceipt(
    string RequestId, DateTimeOffset StartedAt, string Host, int Page, int PageSize,
    bool EmailLookup, bool Incremental, int? HttpStatus, long DurationMs);

/// <summary>Bounded in-memory receipts. Never stores tokens, addresses or response bodies.</summary>
public sealed class UpstreamMailboxRequestJournal
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<UpstreamMailboxRequestReceipt> _requests = new();

    public void Record(UpstreamMailboxRequestReceipt receipt)
    {
        _requests.Enqueue(receipt);
        while (_requests.Count > 100) _requests.TryDequeue(out _);
    }

    public UpstreamMailboxRequestReceipt[] Read() => _requests.ToArray();
}
