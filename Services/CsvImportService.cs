using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services;

public interface ICsvImportService
{
    string QueueImport(CsvImportJob job);
    CsvImportJob? GetJob(string jobId);
    bool HasActiveJobs();
    void CleanupOldJobs();
    bool StartVerification(string jobId);
    bool RetryVerification(string jobId);
}

/// <summary>
/// Performs the slow database part of CSV imports outside the HTTP request.
/// A single worker keeps SQLite writes serialized while the web request returns
/// as soon as structural validation has completed.
/// </summary>
public sealed class CsvImportService : BackgroundService, ICsvImportService
{
    private const int MaxSamples = 100;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CsvImportService> _logger;
    private readonly IMailConcurrencyCoordinator _concurrency;
    private readonly int _automaticLimit;
    private readonly ConcurrentQueue<CsvImportJob> _queue = new();
    private readonly ConcurrentQueue<CsvImportJob> _verificationQueue = new();
    private readonly ConcurrentDictionary<string, CsvImportJob> _jobs = new();
    private readonly Timer _cleanupTimer;

    public CsvImportService(
        IServiceProvider serviceProvider,
        ILogger<CsvImportService> logger,
        IMailConcurrencyCoordinator concurrency,
        IOptions<MailConcurrencyOptions> options)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _concurrency = concurrency;
        _automaticLimit = options.Value.AutomaticLimit;
        _cleanupTimer = new Timer(_ => CleanupOldJobs(), null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
    }

    public string QueueImport(CsvImportJob job)
    {
        job.Status = CsvImportJobStatus.Queued;
        job.LastUpdated = DateTime.UtcNow;
        _jobs[job.JobId] = job;
        _queue.Enqueue(job);
        _logger.LogInformation("Queued CSV credential import {JobId} with {Rows} rows", job.JobId, job.TotalRows);
        return job.JobId;
    }

    public CsvImportJob? GetJob(string jobId)
        => _jobs.TryGetValue(jobId, out var job) ? job : null;

    public bool HasActiveJobs()
        => _jobs.Values.Any(job => job.Status is CsvImportJobStatus.Queued or CsvImportJobStatus.Running or CsvImportJobStatus.Verifying);

    public bool StartVerification(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        lock (job)
        {
            if (job.Status != CsvImportJobStatus.AwaitingVerification || job.VerificationStarted)
                return false;
            job.VerificationStarted = true;
            job.VerificationTotalCount = job.AccountIds.Count;
            job.Status = CsvImportJobStatus.Verifying;
            job.LastUpdated = DateTime.UtcNow;
            _verificationQueue.Enqueue(job);
            return true;
        }
    }

    public bool RetryVerification(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        lock (job)
        {
            if (job.Status != CsvImportJobStatus.CompletedWithErrors || job.VerificationFailedAccountIds.Count == 0)
                return false;
            var retryIds = job.VerificationFailedAccountIds.Distinct().ToList();
            job.VerificationFailedAccountIds.Clear();
            job.VerificationProcessedCount = 0;
            job.VerificationSuccessCount = 0;
            job.VerificationFailedCount = 0;
            job.VerificationFormatFailureCount = 0;
            job.VerificationAuthFailureCount = 0;
            job.VerificationNetworkFailureCount = 0;
            job.VerificationRateLimitCount = 0;
            job.VerificationTotalCount = retryIds.Count;
            job.Status = CsvImportJobStatus.Verifying;
            job.LastUpdated = DateTime.UtcNow;
            job.AccountIds.Clear();
            job.AccountIds.AddRange(retryIds);
            _verificationQueue.Enqueue(job);
            return true;
        }
    }

    public void CleanupOldJobs()
    {
        var cutoff = DateTime.UtcNow.AddDays(-2);
        foreach (var job in _jobs.Values.Where(j => j.Completed.HasValue && j.Completed < cutoff))
            _jobs.TryRemove(job.JobId, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_queue.TryDequeue(out var job))
                    await ProcessJobAsync(job, stoppingToken);
                else if (_verificationQueue.TryDequeue(out var verificationJob))
                    await VerifyJobAsync(verificationJob, stoppingToken);
                else
                    await Task.Delay(100, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var failure = MailConnectionFailurePolicy.Classify(ex);
                _logger.LogError("CSV credential import worker failed: {ErrorCode} ({ExceptionCategory})",
                    failure.Code, failure.ExceptionCategory);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task ProcessJobAsync(CsvImportJob job, CancellationToken cancellationToken)
    {
        job.Status = CsvImportJobStatus.Running;
        job.Started = DateTime.UtcNow;
        job.LastUpdated = DateTime.UtcNow;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var intake = scope.ServiceProvider.GetRequiredService<MailCredentialIntakeService>();
            var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<ICredentialEncryptionService>();

            foreach (var row in job.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!string.IsNullOrWhiteSpace(row.ImportWarning))
                        job.WarningCount++;
                    // Import never connects to a provider. Verification happens on refresh.
                    var result = await intake.UpsertAsync(
                        job.UserId,
                        new MailCredentialIntake(row.Email, row.Password, row.Domain, row.ClientId),
                        enabled: job.Enabled,
                        cancellationToken: cancellationToken,
                        verifyCredential: false,
                        allowCrossUserCredentialUpdate: job.AllowCrossUserCredentialUpdate);

                    // Read through the database again before reporting success. Compare
                    // fingerprints so the credential itself never enters logs or errors.
                    var persistedCredential = await context.MailAccounts
                        .AsNoTracking()
                        .Where(account => account.Id == result.Account.Id)
                        .Select(account => account.Password)
                        .SingleAsync(cancellationToken);
                    var expectedFingerprint = CredentialFingerprint(row.Password);
                    var actualFingerprint = CredentialFingerprint(encryption.Decrypt(persistedCredential ?? string.Empty));
                    if (!CryptographicOperations.FixedTimeEquals(expectedFingerprint, actualFingerprint))
                        throw new InvalidOperationException("凭证写入后校验失败。");

                    var display = new CsvImportCreatedRow
                    {
                        Email = result.Account.EmailAddress,
                        Name = result.Account.Name
                    };
                    if (result.Created)
                    {
                        job.CreatedCount++;
                        AddSample(job.CreatedSamples, display);
                    }
                    else
                    {
                        job.UpdatedCount++;
                        AddSample(job.UpdatedSamples, display);
                    }
                    job.AccountIds.Add(result.Account.Id);
                    job.PendingVerificationCount++;
                    if (result.Account.MailProviderKind == MailProviderKind.Gmail
                        && MailCredentialInputPolicy.Normalize(row.Password).Length != 16)
                    {
                        job.FormatWarningCount++;
                        var persisted = await context.MailAccounts.FindAsync(new object[] { result.Account.Id }, cancellationToken);
                        if (persisted is not null)
                        {
                            persisted.CredentialDetectionStatus = "FormatNeedsConfirmation";
                            await context.SaveChangesAsync(cancellationToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    job.FailedCount++;
                    AddSample(job.FailedSamples, new CsvImportFailedRow
                    {
                        FileName = row.SourceFileName,
                        LineNumber = row.LineNumber,
                        Email = row.Email,
                        Reason = ToSafeFailureReason(ex)
                    });
                }

                job.ProcessedRows++;
                job.LastUpdated = DateTime.UtcNow;
                if (job.ProcessedRows % 100 == 0)
                    context.ChangeTracker.Clear();
            }

            job.Rows.Clear();
            job.PendingVerificationCount = job.AccountIds.Count;
            job.VerificationTotalCount = job.AccountIds.Count;
            job.Status = job.AccountIds.Count > 0
                ? CsvImportJobStatus.AwaitingVerification
                : CsvImportJobStatus.CompletedWithErrors;
            job.Completed = DateTime.UtcNow;
            job.LastUpdated = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(job.UserName))
            {
                var accessLog = scope.ServiceProvider.GetRequiredService<IAccessLogService>();
                await accessLog.LogAccessAsync(job.UserName, AccessLogType.Account,
                    searchParameters: $"Account bulk import: {job.CreatedCount} created, {job.UpdatedCount} updated, {job.SkippedCount} skipped, {job.FailedCount} failed");
            }

            _logger.LogInformation("Completed CSV import {JobId}: {Created} created, {Updated} updated, {Failed} failed",
                job.JobId, job.CreatedCount, job.UpdatedCount, job.FailedCount);
        }
        catch (OperationCanceledException)
        {
            job.Rows.Clear();
            job.Status = CsvImportJobStatus.Failed;
            job.ErrorMessage = "导入任务已停止。";
            job.Completed = DateTime.UtcNow;
            job.LastUpdated = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            job.Rows.Clear();
            job.Status = CsvImportJobStatus.Failed;
            job.ErrorMessage = "导入任务失败，请稍后重试。";
            job.Completed = DateTime.UtcNow;
            job.LastUpdated = DateTime.UtcNow;
            var failure = MailConnectionFailurePolicy.Classify(ex);
            _logger.LogError("CSV import {JobId} failed: {ErrorCode} ({ExceptionCategory})",
                job.JobId, failure.Code, failure.ExceptionCategory);
        }
    }

    private async Task VerifyJobAsync(CsvImportJob job, CancellationToken cancellationToken)
    {
        try
        {
            await Parallel.ForEachAsync(job.AccountIds,
                new ParallelOptions { MaxDegreeOfParallelism = _automaticLimit, CancellationToken = cancellationToken },
                async (accountId, token) =>
            {
                MailAccount? account = null;
                ISyncFailureRecorder? recorder = null;
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
                    recorder = scope.ServiceProvider.GetRequiredService<ISyncFailureRecorder>();
                    account = await context.MailAccounts.FindAsync(new object[] { accountId }, token);
                    if (account is null)
                    {
                        lock (job)
                        {
                            job.VerificationFailedCount++;
                            job.VerificationFailedAccountIds.Add(accountId);
                        }
                        return;
                    }
                    var domain = account.EmailAddress.Split('@').LastOrDefault() ?? "unknown";
                    await using var lease = await _concurrency.AcquireAutomaticAsync(domain, token, account.MailProviderKind);
                    if (account.MailProviderKind == MailProviderKind.Gmail)
                    {
                        var encryption = scope.ServiceProvider.GetRequiredService<ICredentialEncryptionService>();
                        var formatOk = false;
                        try
                        {
                            var credential = MailCredentialInputPolicy.Normalize(encryption.Decrypt(account.Password ?? string.Empty));
                            formatOk = credential.Length == 16;
                        }
                        catch (Exception)
                        {
                            formatOk = false;
                        }
                        if (!formatOk)
                        {
                            account.CredentialDetectionStatus = "FormatNeedsConfirmation";
                            await context.SaveChangesAsync(token);
                            await TryRecordFailureAsync(recorder, job.JobId, account.Id,
                                MailConnectionFailurePolicy.Classify(new MailCredentialFormatException("Invalid Gmail credential format")));
                            lock (job)
                            {
                                job.VerificationFailedCount++;
                                job.VerificationFailedAccountIds.Add(accountId);
                                job.VerificationFormatFailureCount++;
                                AddSample(job.FailedSamples, new CsvImportFailedRow
                                {
                                    Email = account.EmailAddress,
                                    Reason = "Gmail 应用专用密码去除空白和不可见字符后必须恰好是 16 位。"
                                });
                            }
                            return;
                        }
                    }
                    try
                    {
                        var verifier = scope.ServiceProvider.GetRequiredService<IMailCredentialVerifier>();
                        await verifier.VerifyAsync(account, token);
                        await context.SaveChangesAsync(token);
                        lock (job) job.VerificationSuccessCount++;
                    }
                    catch (Exception ex)
                    {
                        account.CredentialDetectionStatus = "VerificationFailed";
                        await context.SaveChangesAsync(token);
                        var failure = MailConnectionFailurePolicy.Classify(ex);
                        await TryRecordFailureAsync(recorder, job.JobId, account.Id, failure);
                        lock (job)
                        {
                            job.VerificationFailedCount++;
                            job.VerificationFailedAccountIds.Add(accountId);
                            switch (failure.Code)
                            {
                                case MailFailureCode.GmailCredentialFormatInvalid: job.VerificationFormatFailureCount++; break;
                                case MailFailureCode.AuthenticationRejected: job.VerificationAuthFailureCount++; break;
                                case MailFailureCode.RateLimited: job.VerificationRateLimitCount++; break;
                                case MailFailureCode.TargetUnavailable or MailFailureCode.ProxyUnavailable or MailFailureCode.TlsFailed:
                                    job.VerificationNetworkFailureCount++; break;
                            }
                            AddSample(job.FailedSamples, new CsvImportFailedRow { Email = account.EmailAddress, Reason = failure.Message });
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var failure = MailConnectionFailurePolicy.Classify(ex);
                    if (account is not null && recorder is not null)
                        await TryRecordFailureAsync(recorder, job.JobId, accountId, failure);
                    lock (job)
                    {
                        job.VerificationFailedCount++;
                        job.VerificationFailedAccountIds.Add(accountId);
                        AddSample(job.FailedSamples, new CsvImportFailedRow
                        {
                            Email = account?.EmailAddress ?? string.Empty,
                            Reason = failure.Message
                        });
                    }
                }
                finally
                {
                    lock (job)
                    {
                        job.VerificationProcessedCount++;
                        job.LastUpdated = DateTime.UtcNow;
                    }
                }
            });
            job.PendingVerificationCount = Math.Max(0, job.AccountIds.Count - job.VerificationProcessedCount);
            job.Status = job.VerificationFailedCount > 0 || job.FailedCount > 0 || job.SkippedCount > 0
                ? CsvImportJobStatus.CompletedWithErrors
                : CsvImportJobStatus.Completed;
            job.Completed = DateTime.UtcNow;
            job.LastUpdated = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            job.Status = CsvImportJobStatus.Failed;
            job.ErrorMessage = "验证任务已停止。";
            job.Completed = DateTime.UtcNow;
            job.LastUpdated = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            job.Status = CsvImportJobStatus.Failed;
            job.ErrorMessage = "验证任务失败，请稍后重试。";
            job.Completed = DateTime.UtcNow;
            job.LastUpdated = DateTime.UtcNow;
            var failure = MailConnectionFailurePolicy.Classify(ex);
            _logger.LogError("CSV verification {JobId} failed: {ErrorCode} ({ExceptionCategory})",
                job.JobId, failure.Code, failure.ExceptionCategory);
        }
    }

    private static void AddSample<T>(List<T> samples, T value)
    {
        if (samples.Count < MaxSamples)
            samples.Add(value);
    }

    private async Task TryRecordFailureAsync(
        ISyncFailureRecorder recorder, string jobId, int accountId, MailFailure failure)
    {
        try
        {
            await recorder.RecordAsync(jobId, accountId, "VerifyCredential", failure);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Failed to persist credential verification failure for job {JobId}: {ExceptionCategory}",
                jobId, MailConnectionFailurePolicy.Classify(exception).ExceptionCategory);
        }
    }

    private static byte[] CredentialFingerprint(string credential)
        => SHA256.HashData(Encoding.UTF8.GetBytes(MailCredentialInputPolicy.Normalize(credential)));

    private static string ToSafeFailureReason(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        if (message.Contains("写入后校验失败", StringComparison.OrdinalIgnoreCase))
            return "凭证写入失败，请重试。";
        if (message.Contains("格式", StringComparison.OrdinalIgnoreCase)
            || message.Contains("16 位", StringComparison.OrdinalIgnoreCase))
            return "凭证格式待确认。";
        if (message.Contains("限制", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too many", StringComparison.OrdinalIgnoreCase))
            return "服务商暂时限制登录，请稍后重试。";
        if (message.Contains("登录校验", StringComparison.OrdinalIgnoreCase)
            || message.Contains("授权码无效", StringComparison.OrdinalIgnoreCase))
            return "认证失败，请检查邮箱与授权码是否匹配。";
        if (message.Contains("连接", StringComparison.OrdinalIgnoreCase)
            || message.Contains("网络", StringComparison.OrdinalIgnoreCase)
            || message.Contains("代理", StringComparison.OrdinalIgnoreCase)
            || message.Contains("TLS", StringComparison.OrdinalIgnoreCase)
            || message.Contains("超时", StringComparison.OrdinalIgnoreCase))
            return "网络或 TLS 连接失败，可稍后重试。";
        return "验证失败，可稍后重试。";
    }

    public override void Dispose()
    {
        _cleanupTimer.Dispose();
        base.Dispose();
    }
}
