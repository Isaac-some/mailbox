using MailArchiver.Models;

namespace MailArchiver.Tests.Services;

public class MailSyncOptionsTests
{
    [Fact]
    public void Default_local_mailbox_cap_is_thirty_messages()
    {
        var options = new MailSyncOptions();

        Assert.Equal(30, options.MaxStoredEmailsPerAccount);
    }
}
