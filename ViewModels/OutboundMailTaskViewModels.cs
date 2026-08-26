using MailArchiver.Models;
using Microsoft.AspNetCore.Http;

namespace MailArchiver.ViewModels;

public sealed class OutboundMailTaskIndexViewModel
{
    public IFormFile? CsvFile { get; set; }
    public List<OutboundMailTaskCsvErrorViewModel> ImportErrors { get; set; } = [];
    public List<OutboundSenderAccountViewModel> SenderAccounts { get; set; } = [];
    public List<OutboundMailTaskSummaryViewModel> Tasks { get; set; } = [];
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
}

public sealed class OutboundSenderAccountViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool CanSend { get; set; }
}

public sealed class OutboundSenderMatchRequest
{
    public string SourceText { get; set; } = string.Empty;
    public IFormFile? ListFile { get; set; }
}

public sealed class OutboundSenderDownloadRequest
{
    public List<int> SelectedAccountIds { get; set; } = [];
}

public sealed record OutboundMailTaskCsvErrorViewModel(int LineNumber, string Reason);

public sealed class OutboundMailTaskSummaryViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Sending { get; set; }
    public int Sent { get; set; }
    public int Failed { get; set; }
    public int Interrupted { get; set; }
    public int Cancelled { get; set; }
}

public sealed class OutboundMailTaskDetailsViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
    public List<OutboundMailTaskItemViewModel> Items { get; set; } = [];

    public bool IsActive => Items.Any(item =>
        item.Status is OutboundMailTaskItemStatus.Pending or OutboundMailTaskItemStatus.Sending);
}

public sealed class OutboundMailTaskItemViewModel
{
    public int Id { get; set; }
    public int CsvRowNumber { get; set; }
    public string Sender { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTime ScheduledAtUtc { get; set; }
    public OutboundMailTaskItemStatus Status { get; set; }
    public string? MessageId { get; set; }
    public string? ErrorMessage { get; set; }
}
