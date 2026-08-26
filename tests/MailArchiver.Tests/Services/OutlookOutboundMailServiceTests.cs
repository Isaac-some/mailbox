using MailArchiver.Models;
using MailArchiver.Services;
using MimeKit;

namespace MailArchiver.Tests.Services;

public class OutboundMailServiceTests
{
    [Fact]
    public void BuildMimeMessage_preserves_recipients_plain_text_and_attachments()
    {
        var account = new MailAccount
        {
            Name = "Personal Outlook",
            EmailAddress = "sender@outlook.com",
            Provider = ProviderType.MSA
        };
        var request = new OutboundMailMessage(
            ["one@example.com", "two@example.com"],
            ["copy@example.com"],
            " Test subject ",
            "line one\nline two",
            [new OutboundAttachment("note.txt", "text/plain", "hello"u8.ToArray())]);

        var message = OutboundMailService.BuildMimeMessage(account, request);

        Assert.Equal("Test subject", message.Subject);
        Assert.Equal("sender@outlook.com", Assert.IsType<MailboxAddress>(Assert.Single(message.From)).Address);
        Assert.Equal(2, message.To.Mailboxes.Count());
        Assert.Single(message.Cc.Mailboxes);
        Assert.Equal("line one\nline two", message.TextBody);
        Assert.Single(message.Attachments);
        Assert.False(string.IsNullOrWhiteSpace(message.MessageId));
    }

    [Fact]
    public void CanAttemptSend_accepts_supported_password_accounts_and_unverified_imported_Outlook_tokens()
    {
        var yahoo = new MailAccount
        {
            EmailAddress = "sender@yahoo.com",
            Provider = ProviderType.IMAP,
            Password = "enc:v1:ciphertext"
        };
        var importedOutlook = new MailAccount
        {
            EmailAddress = "sender@outlook.com",
            Provider = ProviderType.MSA,
            OAuthRefreshToken = "refresh-token",
            OAuthGrantedScopes = null
        };

        Assert.True(OutboundMailService.CanAttemptSend(yahoo));
        Assert.True(OutboundMailService.CanAttemptSend(importedOutlook));

        importedOutlook.OAuthGrantedScopes = MsaOAuthScopePolicy.Imap;
        Assert.False(OutboundMailService.CanAttemptSend(importedOutlook));
    }

    [Fact]
    public void CanAttemptSend_accepts_Gmail_and_Yahoo_OAuth_but_not_GMX_OAuth()
    {
        var gmail = OAuthAccount("sender@gmail.com", clientSecret: null);
        var yahoo = OAuthAccount("sender@yahoo.com", clientSecret: "secret");
        var yahooWithoutSecret = OAuthAccount("sender@yahoo.com", clientSecret: null);
        var gmx = OAuthAccount("sender@gmx.com", clientSecret: "secret");

        Assert.True(OutboundMailService.CanAttemptSend(gmail));
        Assert.True(OutboundMailService.CanAttemptSend(yahoo));
        Assert.False(OutboundMailService.CanAttemptSend(yahooWithoutSecret));
        Assert.False(OutboundMailService.CanAttemptSend(gmx));
    }

    private static MailAccount OAuthAccount(string email, string? clientSecret)
        => new()
        {
            EmailAddress = email,
            Provider = ProviderType.IMAP,
            ClientId = "client-id",
            ClientSecret = clientSecret,
            OAuthRefreshToken = "refresh-token",
            OAuthRedirectUri = email.Contains("yahoo", StringComparison.OrdinalIgnoreCase) ? "oob" : null
        };
}
