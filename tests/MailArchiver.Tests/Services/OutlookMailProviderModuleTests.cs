using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using MimeKit;

namespace MailArchiver.Tests.Services;

public class OutlookMailProviderModuleTests
{
    [Fact]
    public async Task SendAsync_uses_a_Graph_token_and_marks_the_provider_sent_copy_as_saved()
    {
        var tokens = new FakeTokenManager();
        var graph = new FakeGraphSender();
        var module = new OutlookMailProviderModule(tokens, graph);
        var account = new MailAccount
        {
            Id = 42,
            EmailAddress = "sender@outlook.com",
            Username = "sender@outlook.com",
            Provider = ProviderType.MSA,
            MailProviderKind = MailProviderKind.Outlook,
            OAuthRefreshToken = "refresh"
        };
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "body" } };
        message.To.Add(MailboxAddress.Parse("target@example.com"));

        var result = await module.SendAsync(account, message);

        Assert.Equal(1, tokens.GraphRequests);
        Assert.Equal(0, tokens.ImapRequests);
        Assert.Equal("graph-access", graph.AccessToken);
        Assert.True(result.SentCopySavedByProvider);
    }

    private sealed class FakeTokenManager : IMsaTokenManager
    {
        public int GraphRequests { get; private set; }
        public int ImapRequests { get; private set; }

        public Task<MsaAccessToken> GetAccessTokenAsync(
            MailAccount account,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            ImapRequests++;
            return Task.FromResult(new MsaAccessToken(account.EmailAddress, "imap-access"));
        }

        public Task<MsaAccessToken> GetGraphAccessTokenAsync(
            MailAccount account,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            GraphRequests++;
            return Task.FromResult(new MsaAccessToken(account.EmailAddress, "graph-access"));
        }
    }

    private sealed class FakeGraphSender : IOutlookGraphMailSender
    {
        public string? AccessToken { get; private set; }

        public Task SendAsync(
            MimeMessage message,
            string accessToken,
            CancellationToken cancellationToken)
        {
            AccessToken = accessToken;
            return Task.CompletedTask;
        }
    }
}
