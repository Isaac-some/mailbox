using MailKit;

namespace MailArchiver.Services.Providers.Imap;

public static class IncomingMailFolderPolicy
{
    private static readonly HashSet<string> KnownFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "inbox",
        "bulk",
        "junk",
        "junk email",
        "spam",
        "spamverdacht"
    };

    public static bool ShouldSync(string? name, string? fullName, FolderAttributes attributes)
    {
        if (attributes.HasFlag(FolderAttributes.Inbox) ||
            attributes.HasFlag(FolderAttributes.Junk))
        {
            return true;
        }

        return IsKnownFolderName(name) || IsKnownFolderName(GetLeafName(fullName));
    }

    private static bool IsKnownFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && KnownFolderNames.Contains(name.Trim());

    private static string? GetLeafName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return fullName;

        var separatorIndex = Math.Max(fullName.LastIndexOf('/'), fullName.LastIndexOf('.'));
        return separatorIndex >= 0 ? fullName[(separatorIndex + 1)..] : fullName;
    }
}
