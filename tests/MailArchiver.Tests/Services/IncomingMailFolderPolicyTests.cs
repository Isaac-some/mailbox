using MailArchiver.Models;
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
    public void ShouldSync_accepts_selectable_folders(
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
    public void ShouldSync_accepts_sent_drafts_trash_and_archive(string name, FolderAttributes attributes)
    {
        Assert.True(IncomingMailFolderPolicy.ShouldSync(name, name, attributes));
    }

    [Theory]
    [InlineData(FolderAttributes.NoSelect)]
    [InlineData(FolderAttributes.NonExistent)]
    public void ShouldSync_rejects_unreadable_folders(FolderAttributes attributes)
    {
        Assert.False(IncomingMailFolderPolicy.ShouldSync("Container", "Container", attributes));
    }

    [Theory]
    [InlineData("INBOX", FolderAttributes.None, MailboxFolderCategory.Inbox)]
    [InlineData("Bulk", FolderAttributes.None, MailboxFolderCategory.Junk)]
    [InlineData("Spam", FolderAttributes.None, MailboxFolderCategory.Junk)]
    [InlineData("Junk Email", FolderAttributes.None, MailboxFolderCategory.Junk)]
    [InlineData("Courrier indésirable", FolderAttributes.None, MailboxFolderCategory.Junk)]
    [InlineData("Gelöscht", FolderAttributes.None, MailboxFolderCategory.Trash)]
    [InlineData("Gesendete Objekte", FolderAttributes.None, MailboxFolderCategory.Sent)]
    [InlineData("自定义文件夹", FolderAttributes.None, MailboxFolderCategory.Other)]
    [InlineData("任意名称", FolderAttributes.Trash, MailboxFolderCategory.Trash)]
    public void Classifier_normalizes_provider_and_localized_folder_names(
        string name,
        FolderAttributes attributes,
        MailboxFolderCategory expected)
    {
        Assert.Equal(expected, MailboxFolderClassifier.Classify(name, name, attributes));
    }
}
