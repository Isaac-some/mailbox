using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class MailSyncSchedulePolicyTests
{
    [Fact]
    public void Global_seconds_enable_near_real_time_sync()
    {
        var interval = MailSyncSchedulePolicy.ResolveSyncInterval(
            accountIntervalMinutes: null,
            defaultIntervalSeconds: 10,
            legacyDefaultIntervalMinutes: 60);

        Assert.Equal(TimeSpan.FromSeconds(10), interval);
    }

    [Fact]
    public void Explicit_account_minutes_override_global_seconds()
    {
        var interval = MailSyncSchedulePolicy.ResolveSyncInterval(
            accountIntervalMinutes: 2,
            defaultIntervalSeconds: 10,
            legacyDefaultIntervalMinutes: 60);

        Assert.Equal(TimeSpan.FromMinutes(2), interval);
    }

    [Fact]
    public void Legacy_minutes_remain_the_fallback()
    {
        var interval = MailSyncSchedulePolicy.ResolveSyncInterval(
            accountIntervalMinutes: null,
            defaultIntervalSeconds: null,
            legacyDefaultIntervalMinutes: 60);

        Assert.Equal(TimeSpan.FromMinutes(60), interval);
    }

    [Fact]
    public void Failure_retry_is_never_a_tight_loop()
    {
        var interval = MailSyncSchedulePolicy.ResolveFailureRetryInterval(1);

        Assert.Equal(TimeSpan.FromSeconds(30), interval);
    }

    [Fact]
    public void Startup_stagger_spreads_sequential_account_ids()
    {
        var offsets = Enumerable.Range(1, 5000)
            .Select(id => MailSyncSchedulePolicy.ResolveStartupStagger(id, 21600))
            .Select(offset => offset.TotalSeconds)
            .ToList();

        Assert.True(offsets.Min() >= 0);
        Assert.True(offsets.Max() > 20_000);
        Assert.True(offsets.Distinct().Count() > 4_000);
    }

    [Fact]
    public void Never_synced_account_is_due_immediately_instead_of_being_staggered()
    {
        var now = new DateTime(2026, 7, 24, 1, 30, 0, DateTimeKind.Utc);
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var nextRun = MailSyncSchedulePolicy.ResolveInitialNextRun(
            accountId: 42,
            lastSync: epoch,
            nowUtc: now,
            startupStaggerSeconds: 21600);

        Assert.Equal(now, nextRun);
    }

    [Fact]
    public void Previously_synced_account_keeps_restart_stagger()
    {
        var now = new DateTime(2026, 7, 24, 1, 30, 0, DateTimeKind.Utc);

        var nextRun = MailSyncSchedulePolicy.ResolveInitialNextRun(
            accountId: 42,
            lastSync: now.AddHours(-1),
            nowUtc: now,
            startupStaggerSeconds: 21600);

        Assert.True(nextRun > now);
        Assert.True(nextRun < now.AddHours(6));
    }
}
