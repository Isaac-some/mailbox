using System.Diagnostics;
using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class MailSyncTriggerTests
{
    [Fact]
    public async Task Import_signal_wakes_the_worker_without_waiting_for_poll_interval()
    {
        using var trigger = new MailSyncTrigger();
        trigger.RequestSync();
        var stopwatch = Stopwatch.StartNew();

        await trigger.WaitForNextPollAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Repeated_signals_are_coalesced_without_throwing()
    {
        using var trigger = new MailSyncTrigger();

        trigger.RequestSync();
        trigger.RequestSync();
    }
}
