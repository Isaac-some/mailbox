using System.Collections.Concurrent;
using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services;

public interface IMailConcurrencyCoordinator
{
    Task<IAsyncDisposable> AcquireAutomaticAsync(
        string domain, CancellationToken cancellationToken = default, MailProviderKind? provider = null);
    CancellationTokenSource BeginBackgroundVerification(int accountId, CancellationToken token)
        => CancellationTokenSource.CreateLinkedTokenSource(token);
    void EndBackgroundVerification(int accountId) { }
    void PrioritizeAccount(int accountId) { }
    void ReportRateLimit(string domain, MailProviderKind? provider = null) { }
    Task<IAsyncDisposable> AcquireInteractiveAsync(string domain, CancellationToken cancellationToken = default);
}

public sealed class MailConcurrencyCoordinator : IMailConcurrencyCoordinator, IDisposable
{
    private readonly object _priorityLock = new();
    private readonly Dictionary<int, CancellationTokenSource> _background = new();
    private readonly Dictionary<int, DateTimeOffset> _priorityUntil = new();
    private readonly SemaphoreSlim _automatic;
    private readonly int _perDomainLimit;
    private readonly int _perProviderLimit;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _interactiveDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<MailProviderKind, SemaphoreSlim> _providers = new();

    public MailConcurrencyCoordinator(IOptions<MailConcurrencyOptions> options)
    {
        var value = options.Value;
        _automatic = new SemaphoreSlim(value.AutomaticLimit, value.AutomaticLimit);
        _perDomainLimit = value.PerDomainLimit;
        _perProviderLimit = value.PerProviderAutomaticLimit;
    }

    public async Task<IAsyncDisposable> AcquireAutomaticAsync(
        string domain,
        CancellationToken cancellationToken = default,
        MailProviderKind? provider = null)
    {
        var cooldownKey = provider.HasValue ? provider.Value.ToString() : domain.ToLowerInvariant();
        SemaphoreSlim? providerGate = null;
        var providerAcquired = false;
        var automaticAcquired = false;
        try
        {
            if (provider.HasValue)
            {
                var limit = provider == MailProviderKind.Yahoo ? 1 : _perProviderLimit;
                providerGate = _providers.GetOrAdd(provider.Value, _ => new SemaphoreSlim(limit, limit));
                await providerGate.WaitAsync(cancellationToken);
                providerAcquired = true;
            }
            // Check after the provider gate: queued work must also observe a
            // rate limit reported by the request that just released that gate.
            while (_cooldowns.TryGetValue(cooldownKey, out var until) && until > DateTimeOffset.UtcNow)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, (until - DateTimeOffset.UtcNow).TotalMilliseconds)), cancellationToken);
            await _automatic.WaitAsync(cancellationToken);
            automaticAcquired = true;
            var domainGate = DomainGate(domain);
            await domainGate.WaitAsync(cancellationToken);
            return providerGate is null
                ? new Lease(domainGate, _automatic)
                : new Lease(domainGate, _automatic, providerGate);
        }
        catch
        {
            if (automaticAcquired)
                _automatic.Release();
            if (providerAcquired)
                providerGate!.Release();
            throw;
        }
    }

    public async Task<IAsyncDisposable> AcquireInteractiveAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(domain) ? "unknown" : domain.Trim();
        var domainGate = _interactiveDomains.GetOrAdd(key, _ => new SemaphoreSlim(2, 2));
        await domainGate.WaitAsync(cancellationToken);
        return new Lease(domainGate);
    }

    public CancellationTokenSource BeginBackgroundVerification(int accountId, CancellationToken token)
    {
        lock (_priorityLock) {
            var source = CancellationTokenSource.CreateLinkedTokenSource(token);
            _background[accountId] = source;
            if (_priorityUntil.TryGetValue(accountId, out var until) && until > DateTimeOffset.UtcNow) source.Cancel();
            return source;
        }
    }
    public void EndBackgroundVerification(int accountId)
    { lock (_priorityLock) _background.Remove(accountId); }
    public void PrioritizeAccount(int accountId)
    {
        lock (_priorityLock) {
            _priorityUntil[accountId] = DateTimeOffset.UtcNow.AddMinutes(2);
            if (_background.TryGetValue(accountId, out var source)) source.Cancel();
        }
    }

    public void ReportRateLimit(string domain, MailProviderKind? provider = null)
        => _cooldowns[provider.HasValue ? provider.Value.ToString() : domain.ToLowerInvariant()] = DateTimeOffset.UtcNow.AddMinutes(1);

    private SemaphoreSlim DomainGate(string domain)
    {
        var key = string.IsNullOrWhiteSpace(domain) ? "unknown" : domain.Trim().ToLowerInvariant();
        return _domains.GetOrAdd(key, _ => new SemaphoreSlim(_perDomainLimit, _perDomainLimit));
    }

    public void Dispose()
    {
        _automatic.Dispose();
        foreach (var gate in _domains.Values)
            gate.Dispose();
        foreach (var gate in _interactiveDomains.Values) gate.Dispose();
        foreach (var gate in _providers.Values)
            gate.Dispose();
    }

    private sealed class Lease(params SemaphoreSlim[] gates) : IAsyncDisposable
    {
        private SemaphoreSlim[]? _gates = gates;

        public ValueTask DisposeAsync()
        {
            var gatesToRelease = Interlocked.Exchange(ref _gates, null);
            if (gatesToRelease is not null)
            {
                foreach (var gate in gatesToRelease)
                    gate.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
