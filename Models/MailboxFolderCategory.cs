namespace MailArchiver.Models;

public enum MailboxFolderCategory
{
    Other = 0,
    Inbox = 1,
    Sent = 2,
    Junk = 3,
    Trash = 4,
    Drafts = 5,
    Archive = 6
}

public static class MailboxFolderCategoryExtensions
{
    public static int ToExpansionFlag(this MailboxFolderCategory category) => 1 << (int)category;

    public static string ToDisplayName(this MailboxFolderCategory category) => category switch
    {
        MailboxFolderCategory.Inbox => "收件箱",
        MailboxFolderCategory.Sent => "已发送",
        MailboxFolderCategory.Junk => "垃圾箱",
        MailboxFolderCategory.Trash => "已删除",
        MailboxFolderCategory.Drafts => "草稿",
        MailboxFolderCategory.Archive => "归档",
        _ => "其他"
    };
}
