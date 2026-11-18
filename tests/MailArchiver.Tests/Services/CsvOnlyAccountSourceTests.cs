using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Services;

public sealed class CsvOnlyAccountSourceTests
{
    [Fact]
    public async Task Legacy_enabled_settings_cannot_restore_platform_requests_for_any_account_entry()
    {
        var services = new ServiceCollection();
        services.Configure<UpstreamMailboxSyncOptions>(o => {
            o.Enabled = true; o.Endpoint = "https://retired.example/account-credentials";
            o.BearerToken = "expired-test-token";
        });
        // Do not register any HTTP client or credential intake: disabled entrypoints
        // must not resolve network dependencies, even with persisted legacy settings.
        services.AddCsvOnlyAccountSource();
        await using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<IUpstreamMailboxSyncService>();
        foreach (var result in new[] { await source.PullAsync(7),
            await source.PullEmailAsync(7, "imported@outlook.com"),
            await source.PullEmailAsync(7, "unknown@example.invalid") }) {
            Assert.False(result.Enabled); Assert.True(result.Succeeded);
            Assert.Equal(0, result.Created + result.Updated);
        }
        var options = provider.GetRequiredService<IOptions<UpstreamMailboxSyncOptions>>().Value;
        Assert.False(options.Enabled);
        Assert.Empty(options.BearerToken);
        Assert.Empty(options.Endpoint);
    }
}
