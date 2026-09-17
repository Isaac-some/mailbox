using MailArchiver.Models;

namespace MailArchiver.Tests.Services;

public class MailSyncOptionsTests
{
    [Fact]
    public void Default_mailbox_sync_is_fast_and_category_bounded()
    {
        var options = new MailSyncOptions();

        Assert.Equal(7, options.LookbackDays);
        Assert.Equal(10, options.InitialMessagesPerCategory);
        Assert.Equal(30, options.ExpandedMessagesPerCategory);
        Assert.False(options.SyncInboxOnly);
    }

    [Fact]
    public void Request_options_merge_to_the_broader_safe_scope()
    {
        var inbox = new MailSyncRequestOptions(7, MailboxFolderCategory.Inbox, 30);
        var junk = new MailSyncRequestOptions(30, MailboxFolderCategory.Junk, 30);

        var merged = inbox.Merge(junk);

        Assert.Equal(30, merged.LookbackDays);
        Assert.Null(merged.TargetCategory);
        Assert.Null(merged.TargetLimit);
    }
}
