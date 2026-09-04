using System.Collections.Concurrent;
using MailArchiver.Data;
using MailArchiver.Models;
using MailArchiver.Models.ViewModels;

namespace MailArchiver.Services;

public interface ICsvImportService
{
    string QueueImport(CsvImportJob job);
    CsvImportJob? GetJob(string jobId);
    bool HasActiveJobs();
    void CleanupOldJobs();
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
    private readonly ConcurrentQueue<CsvImportJob> _queue = new();
    private readonly ConcurrentDictionary<string, CsvImportJob> _jobs = new();
    private readonly Timer _cleanupTimer;

    public CsvImportService(IServiceProvider serviceProvider, ILogger<CsvImportService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _cleanupTimer = new Timer(_ => CleanupOldJobs(), null, TimeSpan.FromHours(24), TimeSpan.FromHours(24));
    }

    public string QueueImport(CsvImportJob job)
    {
        job.Status = CsvImportJobStatus.Queued;
        _jobs[job.JobId] = job;
        _queue.Enqueue(job);
        _logger.LogInformation("Queued CSV credential import {JobId} with {Rows} rows", job.JobId, job.TotalRows);
        return job.JobId;
    }

    public CsvImportJob? GetJob(string jobId)
        => _jobs.TryGetValue(jobId, out var job) ? job : null;

    public bool HasActiveJobs()
        => _jobs.Values.Any(job => job.Status is CsvImportJobStatus.Queued or CsvImportJobStatus.Running);

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
                else
                    await Task.Delay(100, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in CSV credential import worker");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task ProcessJobAsync(CsvImportJob job, CancellationToken cancellationToken)
    {
        job.Status = CsvImportJobStatus.Running;
        job.Started = DateTime.UtcNow;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var intake = scope.ServiceProvider.GetRequiredService<MailCredentialIntakeService>();
            var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();

            foreach (var row in job.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Import never connects to a provider. Verification happens on refresh.
                    var result = await intake.UpsertAsync(
                        job.UserId,
                        new MailCredentialIntake(row.Email, row.Password, row.Domain, row.ClientId),
                        enabled: job.Enabled,
                        cancellationToken: cancellationToken,
                        verifyCredential: false);

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
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException)
                {
                    job.FailedCount++;
                    AddSample(job.FailedSamples, new CsvImportFailedRow
                    {
                        FileName = row.SourceFileName,
                        LineNumber = row.LineNumber,
                        Email = row.Email,
                        Reason = ex.Message
                    });
                }

                job.ProcessedRows++;
                if (job.ProcessedRows % 100 == 0)
                    context.ChangeTracker.Clear();
            }

            job.Rows.Clear();
            job.Status = job.FailedCount > 0 || job.SkippedCount > 0
                ? CsvImportJobStatus.CompletedWithErrors
                : CsvImportJobStatus.Completed;
            job.Completed = DateTime.UtcNow;

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
        }
        catch (Exception ex)
        {
            job.Rows.Clear();
            job.Status = CsvImportJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.Completed = DateTime.UtcNow;
            _logger.LogError(ex, "CSV import {JobId} failed", job.JobId);
        }
    }

    private static void AddSample<T>(List<T> samples, T value)
    {
        if (samples.Count < MaxSamples)
            samples.Add(value);
    }

    public override void Dispose()
    {
        _cleanupTimer.Dispose();
        base.Dispose();
    }
}
