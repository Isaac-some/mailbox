using MailArchiver.Models;

namespace MailArchiver.Services;

public static class OutlookImportedAccountFactory
{
    public static MailAccount Create(OutlookImportedAccount imported, bool isEnabled)
        => new()
        {
            Name = MailAccountNamePolicy.Derive(imported.Email),
            EmailAddress = imported.Email.Trim(),
            GroupName = string.Empty,
            ImapServer = "outlook.office365.com",
            ImapPort = 993,
            Username = imported.Email.Trim(),
            Password = null,
            UseSSL = true,
            IsEnabled = isEnabled,
            Provider = ProviderType.MSA,
            MailProviderKind = MailProviderKind.Outlook,
            ClientId = imported.ClientId,
            OAuthRefreshToken = imported.RefreshToken,
            OAuthGrantedScopes = null,
            ExcludedFolders = string.Empty,
            DeleteAfterDays = null,
            LocalRetentionDays = null,
            LastSync = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
}
