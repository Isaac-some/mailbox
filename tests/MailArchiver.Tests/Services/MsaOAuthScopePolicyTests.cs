using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class MsaOAuthScopePolicyTests
{
    [Fact]
    public void Requested_scopes_include_IMAP_SMTP_Graph_send_and_offline_access()
    {
        Assert.Contains(MsaOAuthScopePolicy.Imap, MsaOAuthScopePolicy.RequestedScopes);
        Assert.Contains(MsaOAuthScopePolicy.GraphMailSend, MsaOAuthScopePolicy.RequestedScopes);
        Assert.Contains(MsaOAuthScopePolicy.SmtpSend, MsaOAuthScopePolicy.RequestedScopes);
        Assert.Contains("offline_access", MsaOAuthScopePolicy.RequestedScopes);
        Assert.Equal(MsaOAuthScopePolicy.Imap, MsaOAuthScopePolicy.RequestedScopes[0]);
        Assert.Contains(MsaOAuthScopePolicy.Imap, MsaOAuthScopePolicy.ImapRefreshScopes);
        Assert.DoesNotContain(MsaOAuthScopePolicy.GraphMailSend, MsaOAuthScopePolicy.ImapRefreshScopes);
        Assert.Equal([MsaOAuthScopePolicy.SmtpSend, "offline_access"], MsaOAuthScopePolicy.SmtpRefreshScopes);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("https://outlook.office.com/IMAP.AccessAsUser.All", false)]
    [InlineData("https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send", false)]
    [InlineData("Mail.Send offline_access", true)]
    [InlineData("https://graph.microsoft.com/mail.send", true)]
    public void CanSend_requires_explicit_Graph_Mail_Send_scope(string? scopes, bool expected)
        => Assert.Equal(expected, MsaOAuthScopePolicy.CanSend(scopes));
}
