using System.Net;
using System.Text.Json;
using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using MailKit.Security;
using MimeKit;

namespace MailArchiver.Tests.Services;

public class OutlookRestMailSenderTests
{
    [Fact]
    public async Task SendAsync_sends_plain_text_and_asks_Outlook_to_save_a_sent_copy()
    {
        var handler = new CapturingHandler();
        var sender = new OutlookRestMailSender(new SingleClientFactory(new HttpClient(handler)));
        var account = new MailAccount { EmailAddress = "sender@outlook.com" };
        var message = new MimeMessage { Subject = "测试主题", Body = new TextPart("plain") { Text = "测试正文" } };
        message.To.Add(MailboxAddress.Parse("target@gmx.com"));

        await sender.SendAsync(account, message, "access-token", CancellationToken.None);

        Assert.Equal("https://outlook.office.com/api/v2.0/me/sendmail", handler.Uri?.ToString());
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("access-token", handler.AuthorizationParameter);
        Assert.Equal("sender@outlook.com", handler.AnchorMailbox);
        using var json = JsonDocument.Parse(handler.Body);
        Assert.True(json.RootElement.GetProperty("SaveToSentItems").GetBoolean());
        var restMessage = json.RootElement.GetProperty("Message");
        Assert.Equal("测试主题", restMessage.GetProperty("Subject").GetString());
        Assert.Equal("Text", restMessage.GetProperty("Body").GetProperty("ContentType").GetString());
        Assert.Equal("测试正文", restMessage.GetProperty("Body").GetProperty("Content").GetString());
        Assert.Equal("target@gmx.com",
            restMessage.GetProperty("ToRecipients")[0].GetProperty("EmailAddress").GetProperty("Address").GetString());
    }

    [Theory]
    [InlineData("535 5.7.139 Authentication unsuccessful")]
    [InlineData("SmtpClientAuthentication is disabled for the Mailbox")]
    public void Smtp_disabled_detection_only_matches_the_explicit_server_response(string message)
        => Assert.True(OutlookMailProviderModule.IsSmtpClientAuthenticationDisabled(new AuthenticationException(message)));

    [Fact]
    public void Smtp_disabled_detection_does_not_hide_other_authentication_failures()
        => Assert.False(OutlookMailProviderModule.IsSmtpClientAuthenticationDisabled(
            new AuthenticationException("535 5.7.3 Authentication unsuccessful")));

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string? AnchorMailbox { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            AnchorMailbox = request.Headers.GetValues("X-AnchorMailbox").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
