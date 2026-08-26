using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class MsaOAuthScopePolicyTests
{
    [Fact]
    public void Requested_scopes_include_receive_send_and_offline_access()
    {
        Assert.Contains(MsaOAuthScopePolicy.Imap, MsaOAuthScopePolicy.RequestedScopes);
        Assert.Contains(MsaOAuthScopePolicy.Smtp, MsaOAuthScopePolicy.RequestedScopes);
        Assert.Contains("offline_access", MsaOAuthScopePolicy.RequestedScopes);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("https://outlook.office.com/IMAP.AccessAsUser.All", false)]
    [InlineData("https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send", true)]
    [InlineData("https://outlook.office.com/smtp.send", true)]
    public void CanSend_requires_explicit_smtp_scope(string? scopes, bool expected)
        => Assert.Equal(expected, MsaOAuthScopePolicy.CanSend(scopes));
}
