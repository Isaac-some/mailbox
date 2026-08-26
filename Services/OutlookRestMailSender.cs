using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailArchiver.Models;
using MimeKit;

namespace MailArchiver.Services;

public interface IOutlookRestMailSender
{
    Task SendAsync(
        MailAccount account,
        MimeMessage message,
        string accessToken,
        CancellationToken cancellationToken);
}

public sealed class OutlookRestMailSender : IOutlookRestMailSender
{
    private const string SendMailEndpoint = "https://outlook.office.com/api/v2.0/me/sendmail";
    private readonly IHttpClientFactory _httpClientFactory;

    public OutlookRestMailSender(IHttpClientFactory httpClientFactory)
        => _httpClientFactory = httpClientFactory;

    public async Task SendAsync(
        MailAccount account,
        MimeMessage message,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var payload = new SendMailRequest(
            new RestMessage(
                message.Subject,
                new RestBody("Text", message.TextBody ?? string.Empty),
                ToRecipients(message.To),
                ToRecipients(message.Cc),
                ToAttachments(message.Attachments)),
            SaveToSentItems: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, SendMailEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("X-AnchorMailbox", account.EmailAddress);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClientFactory.CreateClient("MsaOAuth")
            .SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (responseBody.Length > 500)
            responseBody = responseBody[..500];
        throw new InvalidOperationException(
            $"Outlook REST 发件失败（HTTP {(int)response.StatusCode}）：{responseBody}");
    }

    private static RestRecipient[] ToRecipients(InternetAddressList addresses)
        => addresses.Mailboxes
            .Select(address => new RestRecipient(new RestEmailAddress(address.Address)))
            .ToArray();

    private static RestAttachment[] ToAttachments(IEnumerable<MimeEntity> attachments)
        => attachments
            .OfType<MimePart>()
            .Select(attachment =>
            {
                using var memory = new MemoryStream();
                attachment.Content.DecodeTo(memory);
                return new RestAttachment(
                    "#Microsoft.OutlookServices.FileAttachment",
                    attachment.FileName ?? "attachment",
                    attachment.ContentType.MimeType,
                    Convert.ToBase64String(memory.ToArray()));
            })
            .ToArray();

    private sealed record SendMailRequest(
        [property: JsonPropertyName("Message")] RestMessage Message,
        [property: JsonPropertyName("SaveToSentItems")] bool SaveToSentItems);

    private sealed record RestMessage(
        [property: JsonPropertyName("Subject")] string Subject,
        [property: JsonPropertyName("Body")] RestBody Body,
        [property: JsonPropertyName("ToRecipients")] RestRecipient[] ToRecipients,
        [property: JsonPropertyName("CcRecipients")] RestRecipient[] CcRecipients,
        [property: JsonPropertyName("Attachments")] RestAttachment[] Attachments);

    private sealed record RestBody(
        [property: JsonPropertyName("ContentType")] string ContentType,
        [property: JsonPropertyName("Content")] string Content);

    private sealed record RestRecipient(
        [property: JsonPropertyName("EmailAddress")] RestEmailAddress EmailAddress);

    private sealed record RestEmailAddress(
        [property: JsonPropertyName("Address")] string Address);

    private sealed record RestAttachment(
        [property: JsonPropertyName("@odata.type")] string Type,
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("ContentType")] string ContentType,
        [property: JsonPropertyName("ContentBytes")] string ContentBytes);
}
