using MailArchiver.Models.ViewModels;

namespace MailArchiver.Models;

public enum CsvImportJobStatus
{
    Queued,
    Running,
    Completed,
    CompletedWithErrors,
    Failed
}

/// <summary>
/// In-memory progress for a CSV credential import. Credential rows are cleared
/// when the worker finishes so the job retains only counts and safe display data.
/// </summary>
public sealed class CsvImportJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? Started { get; set; }
    public DateTime? Completed { get; set; }
    public CsvImportJobStatus Status { get; set; } = CsvImportJobStatus.Queued;
    public int TotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public string? ErrorMessage { get; set; }
    public List<CsvImportCreatedRow> CreatedSamples { get; } = new();
    public List<CsvImportCreatedRow> UpdatedSamples { get; } = new();
    public List<CsvImportSkippedRow> SkippedSamples { get; } = new();
    public List<CsvImportFailedRow> FailedSamples { get; } = new();

    internal List<CsvParsedRow> Rows { get; } = new();
}
