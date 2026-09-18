using MailKit;

namespace MailArchiver.Services.Providers.Imap;

public static class IncomingMailFolderPolicy
{
    public static bool ShouldSync(string? name, string? fullName, FolderAttributes attributes)
    {
        return !attributes.HasFlag(FolderAttributes.NoSelect)
            && !attributes.HasFlag(FolderAttributes.NonExistent);
    }
}
