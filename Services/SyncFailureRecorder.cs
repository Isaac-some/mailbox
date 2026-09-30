using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services;

public interface ISyncFailureRecorder
{
    Task RecordAsync(
        string jobId,
        int accountId,
        string operation,
        MailFailure failure,
        CancellationToken cancellationToken = default);
}

public sealed class SyncFailureRecorder(MailArchiverDbContext context) : ISyncFailureRecorder
{
    public async Task RecordAsync(
        string jobId,
        int accountId,
        string operation,
        MailFailure failure,
        CancellationToken cancellationToken = default)
    {
        context.SyncFailureRecords.Add(new SyncFailureRecord
        {
            JobId = jobId,
            MailAccountId = accountId,
            Operation = operation,
            ErrorCode = failure.Code.ToString(),
            FailureStage = failure.Stage.ToString(),
            ExceptionCategory = failure.ExceptionCategory,
            OccurredAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync(cancellationToken);
    }
}

public sealed class SyncFailureRetentionService(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncFailureRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
                var cutoff = DateTime.UtcNow.AddDays(-7);
                var removed = await context.SyncFailureRecords
                    .Where(record => record.OccurredAtUtc < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);
                if (removed > 0)
                    logger.LogInformation("Removed {Count} expired sync failure record(s)", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to remove expired sync failure records");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
