using MailArchiver.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Services;

public class OnDemandMailSyncQueueTests
{
    private static OnDemandMailSyncQueue CreateQueue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MailSync:MaxConcurrentSyncs"] = "1",
                ["MailSync:TimeoutMinutes"] = "1"
            })
            .Build();

        return new OnDemandMailSyncQueue(
            new ServiceCollection().BuildServiceProvider(),
            configuration,
            new MailConcurrencyCoordinator(Options.Create(new MailConcurrencyOptions())),
            NullLogger<OnDemandMailSyncQueue>.Instance);
    }

    [Fact]
    public void InteractiveRequest_PromotesQueuedBulkRequest()
    {
        using var queue = CreateQueue();

        queue.Enqueue(42, MailSyncRequestPriority.Bulk);
        var status = queue.Enqueue(42, MailSyncRequestPriority.Interactive);

        Assert.Equal(MailSyncQueueState.Queued, status.State);
        Assert.Equal(MailSyncRequestPriority.Interactive, status.Priority);
        Assert.Equal(MailSyncRequestPriority.Interactive, queue.GetStatus(42).Priority);
    }

    [Fact]
    public void ExistingInteractiveRequest_IsNotDowngradedByBulkRequest()
    {
        using var queue = CreateQueue();

        queue.Enqueue(42, MailSyncRequestPriority.Interactive);
        var status = queue.Enqueue(42, MailSyncRequestPriority.Bulk);

        Assert.Equal(MailSyncRequestPriority.Interactive, status.Priority);
        Assert.Equal(MailSyncRequestPriority.Interactive, queue.GetStatus(42).Priority);
    }

    [Fact]
    public void FullResync_IsNotReplacedByASubsequentRefresh()
    {
        using var queue = CreateQueue();
        queue.Enqueue(42, MailSyncRequestPriority.Interactive, MailSyncRequestKind.FullResync);

        var status = queue.Enqueue(42, MailSyncRequestPriority.Interactive);

        Assert.Equal(MailSyncRequestKind.FullResync, status.Kind);
    }
}
