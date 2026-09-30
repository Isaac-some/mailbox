namespace MailArchiver.Models;

public sealed class SyncFailureRecord
{
    public long Id { get; set; }
    public string JobId { get; set; } = string.Empty;
    public int MailAccountId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string FailureStage { get; set; } = string.Empty;
    public string ExceptionCategory { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

