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

        if (account.MailProviderKind == MailProviderKind.Custom)
        {
            var endpointDiscovery = services.GetRequiredService<IMailEndpointDiscoveryService>();
            await endpointDiscovery.DiscoverAsync(account, stoppingToken);
            await dbContext.SaveChangesAsync(stoppingToken);
        }

        var providerFactory = services.GetRequiredService<ProviderEmailServiceFactory>();
        var provider = providerFactory.GetServiceForAccount(account);
        var providerModule = account.MailProviderKind is null
            ? null
            : services.GetRequiredService<MailProviders.IMailProviderRegistry>().For(account);

        if (entry.Kind == MailSyncRequestKind.ValidateConnection)
        {
            var canReceive = false;
            try
            {
                canReceive = await provider.TestConnectionAsync(account);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IMAP capability check failed for account {AccountId}", account.Id);
            }

            var canSend = false;
            if (providerModule is not null)
            {
                try
                {
                    canSend = await providerModule.TestOutgoingConnectionAsync(account, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SMTP capability check failed for account {AccountId}", account.Id);
                }
            }
            UpdateCredentialCapabilities(account, canReceive, canSend);
            await dbContext.SaveChangesAsync(stoppingToken);
            if (canReceive || canSend)
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
            if (providerModule is not null)
            {
                var canSend = await providerModule.TestOutgoingConnectionAsync(account, stoppingToken);
                UpdateCredentialCapabilities(account, canReceive: true, canSend: canSend);
                await dbContext.SaveChangesAsync(stoppingToken);
            }
            var storage = services.GetRequiredService<IAccountStorageService>();
            await storage.RefreshAccountStorageAsync(account.Id);
        }
        catch (Exception ex)
        {
            // A credential can be SMTP-only. Preserve the IMAP failure for the
            // sync job, but still probe SMTP so the classification endpoint can
            // report Smtp instead of collapsing both capabilities to Unknown.
            if (providerModule is not null)
            {
                var canSend = false;
                try
                {
                    canSend = await providerModule.TestOutgoingConnectionAsync(account, stoppingToken);
                }
                catch (Exception probeException)
                {
                    _logger.LogDebug(probeException, "SMTP capability check failed after IMAP sync failure for account {AccountId}", account.Id);
                }
                UpdateCredentialCapabilities(account, canReceive: false, canSend: canSend);
                await dbContext.SaveChangesAsync(stoppingToken);
            }
            syncJobs.CompleteJob(jobId, false, MailConnectionFailurePolicy.ToUserMessage(ex));
            throw;
        }
    }

    private static void UpdateCredentialCapabilities(MailAccount account, bool canReceive, bool canSend)
    {
        account.CredentialLastCheckedAt = DateTime.UtcNow;
        account.CredentialScope = (canReceive, canSend) switch
        {
            (true, true) => MailCredentialScope.ImapAndSmtp,
            (true, false) => MailCredentialScope.Imap,
            (false, true) => MailCredentialScope.Smtp,
            _ => MailCredentialScope.Unknown
        };
        if (account.PreferredIncomingAuth == MailAuthenticationMethod.OAuth2
            || account.PreferredOutgoingAuth == MailAuthenticationMethod.OAuth2)
        {
            account.CredentialKind = MailCredentialKind.OAuth2RefreshToken;
        }
        else if (account.MailProviderKind == MailProviderKind.Gmail
                 && (account.PreferredIncomingAuth == MailAuthenticationMethod.Password
                     || account.PreferredOutgoingAuth == MailAuthenticationMethod.Password))
        {
            account.CredentialKind = MailCredentialKind.GoogleAppPassword;
        }
        else
        {
            account.CredentialKind = (canReceive, canSend) switch
            {
                (true, false) => MailCredentialKind.ImapPassword,
                (false, true) => MailCredentialKind.SmtpPassword,
                (true, true) => MailCredentialKind.SharedMailPassword,
                _ => MailCredentialKind.Unknown
            };
        }
        account.CredentialDetectionStatus = (canReceive, canSend) switch
        {
            (true, true) => "ImapSmtpVerified",
            (true, false) => "ImapVerified",
            (false, true) => "SmtpVerified",
            _ => "VerificationFailed"
        };
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
