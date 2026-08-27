using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MimeKit;

namespace MailArchiver.Services;

public interface IOutlookGraphMailSender
{
    Task SendAsync(
        MimeMessage message,
        string accessToken,
        CancellationToken cancellationToken);
}

public sealed class OutlookGraphMailException : Exception
{
    public OutlookGraphMailException(
        HttpStatusCode statusCode,
        string errorCode,
        string userMessage,
        string technicalMessage)
        : base(technicalMessage)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        UserMessage = userMessage;
    }

    public HttpStatusCode StatusCode { get; }
    public string ErrorCode { get; }
    public string UserMessage { get; }
}

public sealed class OutlookGraphMailSender : IOutlookGraphMailSender
{
    internal const string SendMailEndpoint = "https://graph.microsoft.com/v1.0/me/sendMail";
    private readonly IHttpClientFactory _httpClientFactory;

    public OutlookGraphMailSender(IHttpClientFactory httpClientFactory)
        => _httpClientFactory = httpClientFactory;

    public async Task SendAsync(
        MimeMessage message,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var payload = new SendMailRequest(
            new GraphMessage(
                message.Subject ?? string.Empty,
                new GraphBody("Text", message.TextBody ?? string.Empty),
                ToRecipients(message.To),
                ToRecipients(message.Cc),
                ToAttachments(message.Attachments)),
            SaveToSentItems: true);

        using var request = new HttpRequestMessage(HttpMethod.Post, SendMailEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("client-request-id", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("return-client-request-id", "true");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClientFactory.CreateClient("MsaOAuth")
            .SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Accepted)
            return;

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var (errorCode, detail) = ReadGraphError(responseBody, response.StatusCode);
        throw new OutlookGraphMailException(
            response.StatusCode,
            errorCode,
            ToUserMessage(response.StatusCode, errorCode),
            $"Microsoft Graph sendMail failed (HTTP {(int)response.StatusCode}, {errorCode}): {detail}");
    }

    private static string ToUserMessage(HttpStatusCode statusCode, string errorCode)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || errorCode.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)
            || errorCode.Contains("SendAsDenied", StringComparison.OrdinalIgnoreCase))
        {
            return "Outlook Graph 发件授权失败，请确认 Refresh Token 包含 Mail.Send 权限。";
        }

        if ((int)statusCode is 429 or 503 or 504)
            return "微软 Outlook 发件服务暂时繁忙，请稍后重试。";

        if ((int)statusCode >= 500)
            return "微软 Outlook 发件服务暂时不可用，请稍后重试。";

        if (statusCode == HttpStatusCode.RequestEntityTooLarge)
            return "邮件或附件过大，Microsoft Graph 拒绝接收。";

        if (errorCode.Contains("InvalidRecipients", StringComparison.OrdinalIgnoreCase))
            return "收件邮箱地址被微软拒绝，请检查收件地址。";

        return $"Outlook Graph 发件失败（HTTP {(int)statusCode}，{errorCode}）。";
    }

    private static (string ErrorCode, string Detail) ReadGraphError(string responseBody, HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return ($"GRAPH_HTTP_{(int)statusCode}", Truncate(responseBody));
            }
            var code = error.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString()
                : null;
            var detail = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            return (
                string.IsNullOrWhiteSpace(code) ? $"GRAPH_HTTP_{(int)statusCode}" : code,
                Truncate(detail ?? responseBody));
        }
        catch (JsonException)
        {
            return ($"GRAPH_HTTP_{(int)statusCode}", Truncate(responseBody));
        }
        catch (InvalidOperationException)
        {
            return ($"GRAPH_HTTP_{(int)statusCode}", Truncate(responseBody));
        }
    }

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500];

    private static GraphRecipient[] ToRecipients(InternetAddressList addresses)
        => addresses.Mailboxes
            .Select(address => new GraphRecipient(new GraphEmailAddress(address.Address)))
            .ToArray();

    private static GraphAttachment[] ToAttachments(IEnumerable<MimeEntity> attachments)
        => attachments
            .OfType<MimePart>()
            .Select(attachment =>
            {
                using var memory = new MemoryStream();
                var content = attachment.Content
                    ?? throw new InvalidOperationException($"附件“{attachment.FileName ?? "attachment"}”没有可发送的内容。");
                content.DecodeTo(memory);
                return new GraphAttachment(
                    "#microsoft.graph.fileAttachment",
                    attachment.FileName ?? "attachment",
                    attachment.ContentType.MimeType,
                    Convert.ToBase64String(memory.ToArray()));
            })
            .ToArray();

    private sealed record SendMailRequest(
        [property: JsonPropertyName("message")] GraphMessage Message,
        [property: JsonPropertyName("saveToSentItems")] bool SaveToSentItems);

    private sealed record GraphMessage(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("body")] GraphBody Body,
        [property: JsonPropertyName("toRecipients")] GraphRecipient[] ToRecipients,
        [property: JsonPropertyName("ccRecipients")] GraphRecipient[] CcRecipients,
        [property: JsonPropertyName("attachments")] GraphAttachment[] Attachments);

    private sealed record GraphBody(
        [property: JsonPropertyName("contentType")] string ContentType,
        [property: JsonPropertyName("content")] string Content);

    private sealed record GraphRecipient(
        [property: JsonPropertyName("emailAddress")] GraphEmailAddress EmailAddress);

    private sealed record GraphEmailAddress(
        [property: JsonPropertyName("address")] string Address);

    private sealed record GraphAttachment(
        [property: JsonPropertyName("@odata.type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("contentType")] string ContentType,
        [property: JsonPropertyName("contentBytes")] string ContentBytes);
}
