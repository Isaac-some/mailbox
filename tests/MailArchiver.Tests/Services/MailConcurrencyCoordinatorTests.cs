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
        Assert.All(peakByProvider, providerPeak => Assert.InRange(providerPeak, 1, 2));

        await using var first = await coordinator.AcquireAutomaticAsync("gmail.com");
        await using var second = await coordinator.AcquireAutomaticAsync("gmx.com");
        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.AcquireAutomaticAsync("yahoo.com", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await first.DisposeAsync();
        await using var replacement = await coordinator.AcquireAutomaticAsync("yahoo.com");
    }

    [Fact]
    public async Task Saturated_background_provider_cannot_delay_selected_mailbox_or_another_provider()
    {
        using var coordinator = new MailConcurrencyCoordinator(Options.Create(new MailConcurrencyOptions {
            AutomaticLimit = 8, PerDomainLimit = 2 }));
        await using var a = await coordinator.AcquireAutomaticAsync("gmx.com", provider: MailProviderKind.Gmx);
        // Old shared domain gates block this interactive acquisition when saturated.
        await using var b = await coordinator.AcquireAutomaticAsync("gmx.com");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await using var interactive = await coordinator.AcquireInteractiveAsync("gmx.com", deadline.Token);
        await using var other = await coordinator.AcquireAutomaticAsync("yahoo.com", deadline.Token, MailProviderKind.Yahoo);
    }

    [Fact]
    public async Task Provider_limits_are_shared_across_alias_domains_and_independent_between_providers()
    {
        using var c = new MailConcurrencyCoordinator(Options.Create(new MailConcurrencyOptions()));
        await using var gmx1 = await c.AcquireAutomaticAsync("gmx.es", provider: MailProviderKind.Gmx);
        await using var gmx2 = await c.AcquireAutomaticAsync("gmx.fr", provider: MailProviderKind.Gmx);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var gmx3 = c.AcquireAutomaticAsync("gmx.com", deadline.Token, MailProviderKind.Gmx);
        await using var outlook = await c.AcquireAutomaticAsync("outlook.com", deadline.Token, MailProviderKind.Outlook);
        await using var yahoo = await c.AcquireAutomaticAsync("yahoo.com", deadline.Token, MailProviderKind.Yahoo);
        var yahoo2 = c.AcquireAutomaticAsync("yahoo.es", deadline.Token, MailProviderKind.Yahoo);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gmx3);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => yahoo2);
    }

    [Fact]
    public async Task Already_queued_requests_observe_provider_cooldown_but_interactive_requests_do_not()
    {
        using var c = new MailConcurrencyCoordinator(Options.Create(new MailConcurrencyOptions()));
        await using var first = await c.AcquireAutomaticAsync("yahoo.com", provider: MailProviderKind.Yahoo);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var queued = c.AcquireAutomaticAsync("yahoo.es", deadline.Token, MailProviderKind.Yahoo);
        c.ReportRateLimit("yahoo.com", MailProviderKind.Yahoo);
        await first.DisposeAsync();
        await using var selected = await c.AcquireInteractiveAsync("yahoo.com", deadline.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }

    [Fact]
    public void Selecting_same_account_cancels_background_login_and_keeps_future_background_work_out()
    {
        using var c = CreateCoordinator();
        using var background = c.BeginBackgroundVerification(42, default);
        using var other = c.BeginBackgroundVerification(43, default);
        c.PrioritizeAccount(42);
        Assert.True(background.IsCancellationRequested); Assert.False(other.IsCancellationRequested);
        c.EndBackgroundVerification(42);
        using var later = c.BeginBackgroundVerification(42, default);
        Assert.True(later.IsCancellationRequested);
    }

    private static MailConcurrencyCoordinator CreateCoordinator()
        => new(Options.Create(new MailConcurrencyOptions { AutomaticLimit = 2, PerDomainLimit = 4 }));
}
