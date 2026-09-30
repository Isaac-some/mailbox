using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Services;

public sealed class MailConcurrencyCoordinatorTests
{
    [Fact]
    public async Task Automatic_work_never_uses_reserved_interactive_capacity()
    {
        using var coordinator = CreateCoordinator();
        await using var automatic1 = await coordinator.AcquireAutomaticAsync("gmail.com");
        await using var automatic2 = await coordinator.AcquireAutomaticAsync("gmx.com");
        using var pendingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var automatic3 = coordinator.AcquireAutomaticAsync("yahoo.com", pendingCancellation.Token);
        Assert.False(automatic3.IsCompleted);

        await using var interactive1 = await coordinator.AcquireInteractiveAsync("gmail.com");
        await using var interactive2 = await coordinator.AcquireInteractiveAsync("gmail.com");
        Assert.False(automatic3.IsCompleted);
        await automatic1.DisposeAsync();
        await using var automatic3Lease = await automatic3;
    }

    [Fact]
    public async Task Four_per_domain_connections_include_at_most_two_automatic()
    {
        using var coordinator = CreateCoordinator();
        await using var automatic1 = await coordinator.AcquireAutomaticAsync("gmail.com");
        await using var automatic2 = await coordinator.AcquireAutomaticAsync("gmail.com");
        await using var interactive1 = await coordinator.AcquireInteractiveAsync("gmail.com");
        await using var interactive2 = await coordinator.AcquireInteractiveAsync("gmail.com");
        using var pendingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var fifth = coordinator.AcquireInteractiveAsync("gmail.com", pendingCancellation.Token);
        Assert.False(fifth.IsCompleted);
        await interactive1.DisposeAsync();
        await using var fifthLease = await fifth;
    }

    [Fact]
    public async Task Mixed_provider_automatic_peak_is_two_and_cancelled_wait_does_not_leak_capacity()
    {
        using var coordinator = CreateCoordinator();
        var active = 0;
        var peak = 0;
        var activeByProvider = new int[3];
        var peakByProvider = new int[3];
        var tasks = Enumerable.Range(0, 24).Select(async index =>
        {
            var providerIndex = index % 3;
            var domain = new[] { "gmail.com", "gmx.com", "yahoo.com" }[providerIndex];
            var provider = new[] { MailProviderKind.Gmail, MailProviderKind.Gmx, MailProviderKind.Yahoo }[providerIndex];
            await using var lease = await coordinator.AcquireAutomaticAsync(domain, provider: provider);
            var current = Interlocked.Increment(ref active);
            var providerCurrent = Interlocked.Increment(ref activeByProvider[providerIndex]);
            int observed;
            do
            {
                observed = Volatile.Read(ref peak);
            } while (current > observed && Interlocked.CompareExchange(ref peak, current, observed) != observed);
            do
            {
                observed = Volatile.Read(ref peakByProvider[providerIndex]);
            } while (providerCurrent > observed
                && Interlocked.CompareExchange(ref peakByProvider[providerIndex], providerCurrent, observed) != observed);
            await Task.Delay(10);
            Interlocked.Decrement(ref activeByProvider[providerIndex]);
            Interlocked.Decrement(ref active);
        });
        await Task.WhenAll(tasks);
        Assert.InRange(peak, 1, 2);
        Assert.All(peakByProvider, providerPeak => Assert.Equal(1, providerPeak));

        await using var first = await coordinator.AcquireAutomaticAsync("gmail.com");
        await using var second = await coordinator.AcquireAutomaticAsync("gmx.com");
        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.AcquireAutomaticAsync("yahoo.com", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await first.DisposeAsync();
        await using var replacement = await coordinator.AcquireAutomaticAsync("yahoo.com");
    }

    private static MailConcurrencyCoordinator CreateCoordinator()
        => new(Options.Create(new MailConcurrencyOptions { AutomaticLimit = 2, PerDomainLimit = 4 }));
}
