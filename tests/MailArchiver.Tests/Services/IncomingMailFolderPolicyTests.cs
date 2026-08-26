using MailArchiver.Services.Providers.Imap;
using MailKit;

namespace MailArchiver.Tests.Services;

public class IncomingMailFolderPolicyTests
{
    [Theory]
    [InlineData("INBOX", "INBOX", FolderAttributes.None)]
    [InlineData("Bulk", "Bulk", FolderAttributes.None)]
    [InlineData("Spam", "Mailbox/Spam", FolderAttributes.None)]
    [InlineData("Any server name", "Any server name", FolderAttributes.Junk)]
    public void ShouldSync_accepts_inbox_and_junk_folders(
        string name,
        string fullName,
        FolderAttributes attributes)
    {
        Assert.True(IncomingMailFolderPolicy.ShouldSync(name, fullName, attributes));
    }

    [Theory]
    [InlineData("Sent", FolderAttributes.Sent)]
    [InlineData("Draft", FolderAttributes.Drafts)]
    [InlineData("Trash", FolderAttributes.Trash)]
    [InlineData("Archive", FolderAttributes.Archive)]
    public void ShouldSync_rejects_non_receiving_folders(string name, FolderAttributes attributes)
    {
        Assert.False(IncomingMailFolderPolicy.ShouldSync(name, name, attributes));
    }
}
