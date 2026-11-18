using MailArchiver.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MailArchiver.Services;

/// <summary>Retired platform credentials must never be used, including with legacy settings.</summary>
public static class CsvOnlyAccountSource
{
    public static IServiceCollection AddCsvOnlyAccountSource(this IServiceCollection services)
    {
        services.PostConfigure<UpstreamMailboxSyncOptions>(o => {
            o.Enabled = false; o.Endpoint = string.Empty; o.BearerToken = string.Empty;
        });
        services.RemoveAll<IUpstreamMailboxSyncService>();
        services.AddSingleton<IUpstreamMailboxSyncService, DisabledPlatformAccountSource>();
        return services;
    }

    private sealed class DisabledPlatformAccountSource : IUpstreamMailboxSyncService
    {
        public Task<UpstreamMailboxSyncResult> PullAsync(int userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpstreamMailboxSyncResult(false, 0, 0, 0));
        public Task<UpstreamMailboxSyncResult> PullEmailAsync(int userId, string email, CancellationToken cancellationToken = default)
            => PullAsync(userId, cancellationToken);
    }
}
