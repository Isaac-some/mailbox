namespace MailArchiver.Models;

public enum OutboundMailTaskTimingMode
{
    SendImmediately,
    UseCsvSchedule
}

public sealed class OutboundMailTask
{
    public int Id { get; set; }
    public int CreatedByUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public User CreatedByUser { get; set; } = null!;
    public ICollection<OutboundMailTaskItem> Items { get; set; } = new List<OutboundMailTaskItem>();
}

public sealed class OutboundMailTaskItem
{
    public int Id { get; set; }
    public int OutboundMailTaskId { get; set; }
    public int MailAccountId { get; set; }
    public int CsvRowNumber { get; set; }
    public DateTime ScheduledAtUtc { get; set; }
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public OutboundMailTaskItemStatus Status { get; set; } = OutboundMailTaskItemStatus.Pending;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? MessageId { get; set; }
    public bool? SentCopySaved { get; set; }
    public string? ErrorMessage { get; set; }

    public OutboundMailTask OutboundMailTask { get; set; } = null!;
    public MailAccount MailAccount { get; set; } = null!;
}

public enum OutboundMailTaskItemStatus
{
    Pending,
    Sending,
    Sent,
    SentWithoutCopy,
    Failed,
    Interrupted,
    Cancelled
}
