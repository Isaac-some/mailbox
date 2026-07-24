using MailArchiver.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
}
