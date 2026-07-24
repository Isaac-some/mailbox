using System.Collections.Concurrent;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Services.Factories;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services;

public enum MailSyncRequestPriority
{
    Bulk = 0,
    Interactive = 1
}

public enum MailSyncRequestKind
{
    ValidateConnection,
    Synchronize
}

public enum MailSyncQueueState
{
    NotQueued,
    Queued,
    Running
}

public sealed record MailSyncQueueStatus(
    int AccountId,
    MailSyncQueueState State,
    MailSyncRequestKind? Kind,
    MailSyncRequestPriority? Priority);

public interface IOnDemandMailSyncQueue
{
    MailSyncQueueStatus Enqueue(
        int accountId,
        MailSyncRequestPriority priority,
        MailSyncRequestKind kind = MailSyncRequestKind.Synchronize);

    MailSyncQueueStatus GetStatus(int accountId);
}

/// <summary>
/// The only execution path for user-triggered mailbox work. It prevents duplicate
/// syncs per account and gives inbox views precedence over bulk operations.
/// </summary>
public sealed class OnDemandMailSyncQueue : BackgroundService, IOnDemandMailSyncQueue
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OnDemandMailSyncQueue> _logger;
    private readonly int _maxConcurrentSyncs;
    private readonly TimeSpan _timeout;
    private readonly ConcurrentQueue<QueueRequest> _interactive = new();
    private readonly ConcurrentQueue<QueueRequest> _bulk = new();
    private readonly ConcurrentDictionary<int, QueueEntry> _entries = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly object _queueLock = new();

    public OnDemandMailSyncQueue(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<OnDemandMailSyncQueue> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _maxConcurrentSyncs = Math.Max(1, configuration.GetValue("MailSync:MaxConcurrentSyncs", 4));
        _timeout = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("MailSync:TimeoutMinutes", 120)));
    }

    public MailSyncQueueStatus Enqueue(
        int accountId,
        MailSyncRequestPriority priority,
        MailSyncRequestKind kind = MailSyncRequestKind.Synchronize)
    {
        lock (_queueLock)
        {
            if (_entries.TryGetValue(accountId, out var existing))
            {
                if (existing.State == MailSyncQueueState.Running)
                {
                    return existing.ToStatus();
                }

                // A mailbox opened by a user must not remain behind a bulk job.
                if (priority <= existing.Priority && kind == existing.Kind)
                {
                    return existing.ToStatus();
                }
            }

            var entry = new QueueEntry(
                Guid.NewGuid(),
                accountId,
                MailSyncQueueState.Queued,
                priority,
                kind);
            _entries[accountId] = entry;

            var request = new QueueRequest(entry.Token, accountId);
            if (priority == MailSyncRequestPriority.Interactive)
                _interactive.Enqueue(request);
            else
                _bulk.Enqueue(request);

            _available.Release();
            return entry.ToStatus();
        }
    }

    public MailSyncQueueStatus GetStatus(int accountId)
    {
        return _entries.TryGetValue(accountId, out var entry)
            ? entry.ToStatus()
            : new MailSyncQueueStatus(accountId, MailSyncQueueState.NotQueued, null, null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "On-demand mail sync queue started with a maximum of {Concurrency} concurrent task(s)",
            _maxConcurrentSyncs);

        var workers = Enumerable.Range(0, _maxConcurrentSyncs)
            .Select(_ => RunWorkerAsync(stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _available.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!TryDequeueCurrent(out var entry))
                continue;

            try
            {
                await ExecuteRequestAsync(entry, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "On-demand {Kind} failed for account {AccountId}",
                    entry.Kind,
                    entry.AccountId);
            }
            finally
            {
                _entries.TryRemove(new KeyValuePair<int, QueueEntry>(entry.AccountId, entry));
            }
        }
    }

    private bool TryDequeueCurrent(out QueueEntry entry)
    {
        entry = default!;
        while (_interactive.TryDequeue(out var interactiveRequest) || _bulk.TryDequeue(out interactiveRequest))
        {
            if (!_entries.TryGetValue(interactiveRequest.AccountId, out var candidate)
                || candidate.Token != interactiveRequest.Token
                || candidate.State != MailSyncQueueState.Queued)
            {
                continue;
            }

            entry = candidate with { State = MailSyncQueueState.Running };
            if (_entries.TryUpdate(entry.AccountId, entry, candidate))
                return true;
        }

        return false;
    }

    private async Task ExecuteRequestAsync(QueueEntry entry, CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var services = scope.ServiceProvider;
        var dbContext = services.GetRequiredService<MailArchiverDbContext>();
        var account = await dbContext.MailAccounts.FindAsync(new object[] { entry.AccountId }, stoppingToken);
        if (account is null || !account.IsEnabled || account.Provider == ProviderType.IMPORT)
        {
            _logger.LogWarning("Skipping queued mail task for unavailable account {AccountId}", entry.AccountId);
            return;
        }

        var providerFactory = services.GetRequiredService<ProviderEmailServiceFactory>();
        var provider = providerFactory.GetServiceForAccount(account);

        if (entry.Kind == MailSyncRequestKind.ValidateConnection)
        {
            var connected = await provider.TestConnectionAsync(account);
            if (connected)
            {
                _logger.LogInformation("Imported account {AccountId} connection validation succeeded", account.Id);
            }
            else
            {
                _logger.LogWarning("Imported account {AccountId} connection validation failed", account.Id);
            }
            return;
        }

        var syncJobs = services.GetRequiredService<ISyncJobService>();
        var jobId = await syncJobs.StartSyncAsync(account.Id, account.Name, account.LastSync);
        if (jobId is null)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(_timeout);
        syncJobs.UpdateJobProgress(jobId, job => job.CancellationTokenSource = timeout);
        using var timeoutRegistration = timeout.Token.Register(() => syncJobs.CancelJob(jobId));

        try
        {
            await provider.SyncMailAccountAsync(account, jobId);
            var storage = services.GetRequiredService<IAccountStorageService>();
            await storage.RefreshAccountStorageAsync(account.Id);
        }
        catch (Exception ex)
        {
            syncJobs.CompleteJob(jobId, false, ex.Message);
            throw;
        }
    }

    public override void Dispose()
    {
        _available.Dispose();
        base.Dispose();
    }

    private sealed record QueueRequest(Guid Token, int AccountId);

    private sealed record QueueEntry(
        Guid Token,
        int AccountId,
        MailSyncQueueState State,
        MailSyncRequestPriority Priority,
        MailSyncRequestKind Kind)
    {
        public MailSyncQueueStatus ToStatus() => new(AccountId, State, Kind, Priority);
    }
}
