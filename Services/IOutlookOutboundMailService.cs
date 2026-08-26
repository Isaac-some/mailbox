using MailArchiver.Models;

namespace MailArchiver.Services;

public sealed record OutboundAttachment(string FileName, string ContentType, byte[] Content);

public sealed record OutboundMailMessage(
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    string Subject,
    string Body,
    IReadOnlyList<OutboundAttachment> Attachments);

public sealed record OutboundMailResult(bool SentCopySaved, string MessageId);

public interface IOutboundMailService
{
    Task<OutboundMailResult> SendAsync(
        MailAccount account,
        OutboundMailMessage message,
        CancellationToken cancellationToken = default);
}
