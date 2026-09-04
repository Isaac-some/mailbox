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
        Assert.Equal(MailAuthenticationMethod.OAuth2, account.PreferredOutgoingAuth);
    }

    [Fact]
    public async Task SendAsync_falls_back_to_SMTP_OAuth_when_Graph_authorization_is_rejected()
    {
        var tokens = new FakeTokenManager();
        var graph = new FakeGraphSender
        {
            Failure = new OutlookGraphMailException(
                System.Net.HttpStatusCode.Forbidden,
                "Authorization_RequestDenied",
                "denied",
                "denied")
        };
        var smtp = new FakeSmtpSender();
        var module = new OutlookMailProviderModule(tokens, graph, smtp);
        var account = CreateAccount();
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "body" } };
        message.To.Add(MailboxAddress.Parse("target@example.com"));

        var result = await module.SendAsync(account, message);

        Assert.Equal(1, tokens.SmtpRequests);
        Assert.Equal("smtp-access", smtp.AccessToken);
        Assert.False(result.SentCopySavedByProvider);
    }

    [Fact]
    public async Task SendAsync_falls_back_to_SMTP_password_after_both_OAuth_paths_are_rejected()
    {
        var tokens = new FakeTokenManager
        {
            SmtpFailure = new MailKit.Security.AuthenticationException("smtp oauth rejected")
        };
        var graph = new FakeGraphSender
        {
            Failure = new OutlookGraphMailException(
                System.Net.HttpStatusCode.Forbidden,
                "Authorization_RequestDenied",
                "denied",
                "denied")
        };
        var smtp = new FakeSmtpSender();
        var module = new OutlookMailProviderModule(tokens, graph, smtp, new PassthroughEncryption());
        var account = CreateAccount();
        account.Password = "encrypted-password";
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "body" } };
        message.To.Add(MailboxAddress.Parse("target@example.com"));

        var result = await module.SendAsync(account, message);

        Assert.Equal(1, tokens.SmtpRequests);
        Assert.Equal("encrypted-password", smtp.Password);
        Assert.False(result.SentCopySavedByProvider);
        Assert.Equal(MailAuthenticationMethod.Password, account.PreferredOutgoingAuth);
    }

    [Fact]
    public async Task SendAsync_tries_the_remembered_password_route_first()
    {
        var tokens = new FakeTokenManager();
        var smtp = new FakeSmtpSender();
        var module = new OutlookMailProviderModule(tokens, new FakeGraphSender(), smtp, new PassthroughEncryption());
        var account = CreateAccount();
        account.Password = "encrypted-password";
        account.PreferredOutgoingAuth = MailAuthenticationMethod.Password;
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "body" } };
        message.To.Add(MailboxAddress.Parse("target@example.com"));

        await module.SendAsync(account, message);

        Assert.Equal("encrypted-password", smtp.Password);
        Assert.Equal(0, tokens.GraphRequests);
        Assert.Equal(0, tokens.SmtpRequests);
        Assert.Equal(MailAuthenticationMethod.Password, account.PreferredOutgoingAuth);
    }

    [Fact]
    public async Task TestOutgoingConnectionAsync_falls_back_after_OAuth_throws_and_remembers_password()
    {
        var tokens = new FakeTokenManager
        {
            SmtpFailure = new MailKit.Security.AuthenticationException("smtp oauth rejected")
        };
        var smtp = new FakeSmtpSender();
        var module = new OutlookMailProviderModule(tokens, new FakeGraphSender(), smtp, new PassthroughEncryption());
        var account = CreateAccount();
        account.Password = "encrypted-password";

        var connected = await module.TestOutgoingConnectionAsync(account);

        Assert.True(connected);
        Assert.Equal(1, tokens.SmtpRequests);
        Assert.Equal("encrypted-password", smtp.Password);
        Assert.Equal(MailAuthenticationMethod.Password, account.PreferredOutgoingAuth);
    }

    [Fact]
    public async Task TestOutgoingConnectionAsync_tries_remembered_password_before_OAuth()
    {
        var tokens = new FakeTokenManager();
        var smtp = new FakeSmtpSender();
        var module = new OutlookMailProviderModule(tokens, new FakeGraphSender(), smtp, new PassthroughEncryption());
        var account = CreateAccount();
        account.Password = "encrypted-password";
        account.PreferredOutgoingAuth = MailAuthenticationMethod.Password;

        var connected = await module.TestOutgoingConnectionAsync(account);

        Assert.True(connected);
        Assert.Equal(0, tokens.SmtpRequests);
        Assert.Equal("encrypted-password", smtp.Password);
        Assert.Equal(MailAuthenticationMethod.Password, account.PreferredOutgoingAuth);
    }

    [Fact]
    public async Task SendAsync_does_not_send_again_after_an_ambiguous_Graph_network_failure()
    {
        var tokens = new FakeTokenManager();
        var smtp = new FakeSmtpSender();
        var module = new OutlookMailProviderModule(tokens,
            new FakeGraphSender { Failure = new HttpRequestException("response lost after submission") },
            smtp, new PassthroughEncryption());
        var account = CreateAccount();
        account.Password = "password";

        await Assert.ThrowsAsync<HttpRequestException>(() => module.SendAsync(account, new MimeMessage()));

        Assert.Equal(0, tokens.SmtpRequests);
        Assert.Null(smtp.Password);
    }

    [Fact]
    public void Inspect_accepts_password_only_Outlook_accounts()
    {
        var module = new OutlookMailProviderModule(null!, null!);
        var account = CreateAccount();
        account.OAuthRefreshToken = null;
        account.Password = "encrypted-password";

        var capabilities = module.Inspect(account);

        Assert.True(capabilities.CanReceive);
        Assert.True(capabilities.CanSend);
    }

    private static MailAccount CreateAccount() => new()
    {
        Id = 42,
        EmailAddress = "sender@outlook.com",
        Username = "sender@outlook.com",
        Provider = ProviderType.MSA,
        MailProviderKind = MailProviderKind.Outlook,
        OAuthRefreshToken = "refresh"
    };

    private sealed class FakeTokenManager : IMsaTokenManager
    {
        public int GraphRequests { get; private set; }
        public int ImapRequests { get; private set; }
        public int SmtpRequests { get; private set; }
        public Exception? SmtpFailure { get; init; }

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

        public Task<MsaAccessToken> GetSmtpAccessTokenAsync(
            MailAccount account,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            SmtpRequests++;
            if (SmtpFailure is not null)
                throw SmtpFailure;
            return Task.FromResult(new MsaAccessToken(account.EmailAddress, "smtp-access"));
        }
    }

    private sealed class FakeGraphSender : IOutlookGraphMailSender
    {
        public string? AccessToken { get; private set; }
        public Exception? Failure { get; init; }

        public Task SendAsync(
            MimeMessage message,
            string accessToken,
            CancellationToken cancellationToken)
        {
            AccessToken = accessToken;
            if (Failure is not null)
                throw Failure;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSmtpSender : IOutlookSmtpMailSender
    {
        public string? AccessToken { get; private set; }
        public string? Password { get; private set; }

        public Task SendAsync(
            MailAccount account,
            MimeMessage message,
            MsaAccessToken token,
            CancellationToken cancellationToken)
        {
            AccessToken = token.AccessToken;
            return Task.CompletedTask;
        }

        public Task SendWithPasswordAsync(
            MailAccount account,
            MimeMessage message,
            string password,
            CancellationToken cancellationToken)
        {
            Password = password;
            return Task.CompletedTask;
        }

        public Task<bool> TestAsync(
            MailAccount account,
            MsaAccessToken token,
            CancellationToken cancellationToken)
        {
            AccessToken = token.AccessToken;
            return Task.FromResult(true);
        }

        public Task<bool> TestWithPasswordAsync(
            MailAccount account,
            string password,
            CancellationToken cancellationToken)
        {
            Password = password;
            return Task.FromResult(true);
        }
    }

    private sealed class PassthroughEncryption : ICredentialEncryptionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string encryptedValue) => encryptedValue;
    }
}
