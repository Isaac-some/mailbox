using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class SupportedMailProviderPolicyTests
{
    [Theory]
    [InlineData("reader@yahoo.com", "Yahoo", "imap.mail.yahoo.com")]
    [InlineData("reader@yahoo.co.jp", "Yahoo", "imap.mail.yahoo.com")]
    [InlineData("reader@gmx.com", "GMX", "imap.gmx.com")]
    [InlineData("reader@gmx.net", "GMX", "imap.gmx.net")]
    [InlineData("reader@gmx.de", "GMX", "imap.gmx.net")]
    public void TryResolve_uses_the_safe_IMAP_preset_for_supported_addresses(
        string email, string provider, string server)
    {
        var resolved = SupportedMailProviderPolicy.TryResolve(email, out var preset);

        Assert.True(resolved);
        Assert.Equal(provider, preset.Provider);
        Assert.Equal(server, preset.ImapServer);
        Assert.Equal(993, preset.ImapPort);
        Assert.True(preset.UseSsl);
    }

    [Theory]
    [InlineData("reader@gmail.com")]
    [InlineData("reader@outlook.com")]
    [InlineData("reader@example.com")]
    [InlineData("not-an-email")]
    public void TryResolve_rejects_unsupported_or_invalid_addresses(string email)
    {
        var resolved = SupportedMailProviderPolicy.TryResolve(email, out _);

        Assert.False(resolved);
    }
}
