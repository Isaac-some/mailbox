using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class ExternalOAuthProviderPolicyTests
{
    [Theory]
    [InlineData("user@gmail.com", "Google", "https://oauth2.googleapis.com/token", false, false)]
    [InlineData("user@googlemail.com", "Google", "https://oauth2.googleapis.com/token", false, false)]
    [InlineData("user@yahoo.com", "Yahoo", "https://api.login.yahoo.com/oauth2/get_token", true, true)]
    [InlineData("user@yahoo.co.jp", "Yahoo", "https://api.login.yahoo.com/oauth2/get_token", true, true)]
    public void TryResolve_returns_only_approved_OAuth_endpoints(
        string email, string provider, string tokenEndpoint, bool requiresClientSecret, bool requiresRedirectUri)
    {
        var resolved = ExternalOAuthProviderPolicy.TryResolve(email, out var preset);

        Assert.True(resolved);
        Assert.Equal(provider, preset.Provider);
        Assert.Equal(tokenEndpoint, preset.TokenEndpoint);
        Assert.Equal(requiresClientSecret, preset.RequiresClientSecret);
        Assert.Equal(requiresRedirectUri, preset.RequiresRedirectUri);
    }

    [Theory]
    [InlineData("user@gmx.com")]
    [InlineData("user@example.com")]
    [InlineData("not-an-email")]
    public void TryResolve_rejects_providers_without_documented_mail_OAuth(string email)
    {
        Assert.False(ExternalOAuthProviderPolicy.TryResolve(email, out _));
    }
}
