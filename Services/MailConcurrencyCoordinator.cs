using System.Collections.Concurrent;
using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services;

public interface IMailConcurrencyCoordinator
{
    Task<IAsyncDisposable> AcquireAutomaticAsync(
        string domain, CancellationToken cancellationToken = default, MailProviderKind? provider = null);
    Task<IAsyncDisposable> AcquireInteractiveAsync(string domain, CancellationToken cancellationToken = default);
}

public sealed class MailConcurrencyCoordinator : IMailConcurrencyCoordinator, IDisposable
{
    private readonly SemaphoreSlim _automatic;
    private readonly int _perDomainLimit;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<MailProviderKind, SemaphoreSlim> _providers = new();

    public MailConcurrencyCoordinator(IOptions<MailConcurrencyOptions> options)
    {
        var value = options.Value;
        _automatic = new SemaphoreSlim(value.AutomaticLimit, value.AutomaticLimit);
        _perDomainLimit = value.PerDomainLimit;
    }

    public async Task<IAsyncDisposable> AcquireAutomaticAsync(
        string domain,
        CancellationToken cancellationToken = default,
        MailProviderKind? provider = null)
    {
        SemaphoreSlim? providerGate = null;
        var providerAcquired = false;
        var automaticAcquired = false;
        try
        {
            if (provider.HasValue)
            {
                providerGate = _providers.GetOrAdd(provider.Value, _ => new SemaphoreSlim(1, 1));
                await providerGate.WaitAsync(cancellationToken);
                providerAcquired = true;
            }
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
        var domainGate = DomainGate(domain);
        await domainGate.WaitAsync(cancellationToken);
        return new Lease(domainGate);
    }

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
