using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public class SupportedMailProviderPolicyTests
{
    [Theory]
    [InlineData("reader@yahoo.com", "Yahoo", "imap.mail.yahoo.com", "smtp.mail.yahoo.com")]
    [InlineData("reader@yahoo.co.jp", "Yahoo", "imap.mail.yahoo.com", "smtp.mail.yahoo.com")]
    [InlineData("reader@gmx.com", "GMX", "imap.gmx.com", "mail.gmx.com")]
    [InlineData("reader@gmx.net", "GMX", "imap.gmx.net", "mail.gmx.net")]
    [InlineData("reader@gmx.de", "GMX", "imap.gmx.net", "mail.gmx.net")]
    [InlineData("reader@gmail.com", "Gmail", "imap.gmail.com", "smtp.gmail.com")]
    [InlineData("reader@googlemail.com", "Gmail", "imap.gmail.com", "smtp.gmail.com")]
    public void TryResolve_uses_the_safe_IMAP_preset_for_supported_addresses(
        string email, string provider, string server, string smtpServer)
    {
        var resolved = SupportedMailProviderPolicy.TryResolve(email, out var preset);

        Assert.True(resolved);
        Assert.Equal(provider, preset.Provider);
        Assert.Equal(server, preset.ImapServer);
        Assert.Equal(993, preset.ImapPort);
        Assert.True(preset.UseSsl);
        Assert.Equal(smtpServer, preset.SmtpServer);
        Assert.Equal(587, preset.SmtpPort);
        Assert.True(preset.UseStartTls);
        Assert.Equal(provider == "Gmail", preset.SmtpSavesSentCopy);
    }

    [Theory]
    [InlineData("reader@outlook.com")]
    [InlineData("reader@example.com")]
    [InlineData("not-an-email")]
    public void TryResolve_rejects_unsupported_or_invalid_addresses(string email)
    {
        var resolved = SupportedMailProviderPolicy.TryResolve(email, out _);

        Assert.False(resolved);
    }
}
