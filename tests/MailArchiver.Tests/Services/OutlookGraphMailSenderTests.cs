using System.Net;
using System.Text.Json;
using MailArchiver.Services;
using MimeKit;

namespace MailArchiver.Tests.Services;

public class OutlookGraphMailSenderTests
{
    [Fact]
    public async Task SendAsync_uses_Microsoft_Graph_and_asks_it_to_save_a_sent_copy()
    {
        var handler = new CapturingHandler(HttpStatusCode.Accepted);
        var sender = new OutlookGraphMailSender(new SingleClientFactory(new HttpClient(handler)));
        var message = new MimeMessage { Subject = "测试主题", Body = new TextPart("plain") { Text = "测试正文" } };
        message.To.Add(MailboxAddress.Parse("target@gmx.com"));

        await sender.SendAsync(message, "access-token", CancellationToken.None);

        Assert.Equal(OutlookGraphMailSender.SendMailEndpoint, handler.Uri?.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("access-token", handler.AuthorizationParameter);
        using var json = JsonDocument.Parse(handler.Body);
        Assert.True(json.RootElement.GetProperty("saveToSentItems").GetBoolean());
        var graphMessage = json.RootElement.GetProperty("message");
        Assert.Equal("测试主题", graphMessage.GetProperty("subject").GetString());
        Assert.Equal("Text", graphMessage.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("测试正文", graphMessage.GetProperty("body").GetProperty("content").GetString());
        Assert.Equal("target@gmx.com",
            graphMessage.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
    }

    [Fact]
    public async Task SendAsync_returns_a_safe_permission_error_for_Graph_forbidden()
    {
        var handler = new CapturingHandler(
            HttpStatusCode.Forbidden,
            "{\"error\":{\"code\":\"ErrorAccessDenied\",\"message\":\"internal detail\"}}");
        var sender = new OutlookGraphMailSender(new SingleClientFactory(new HttpClient(handler)));
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "body" } };
        message.To.Add(MailboxAddress.Parse("target@gmx.com"));

        var error = await Assert.ThrowsAsync<OutlookGraphMailException>(
            () => sender.SendAsync(message, "access-token", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("ErrorAccessDenied", error.ErrorCode);
        Assert.Contains("Mail.Send", error.UserMessage);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler(HttpStatusCode statusCode, string? responseBody = null) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody ?? string.Empty)
            };
        }
    }
}
