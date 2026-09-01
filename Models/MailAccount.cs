using MailArchiver.Models;
using System.ComponentModel.DataAnnotations.Schema;

public class MailAccount
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string EmailAddress { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string? ImapServer { get; set; }
    public int? ImapPort { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseSSL { get; set; }
    public string? SmtpServer { get; set; }
    public int? SmtpPort { get; set; }
    public bool? SmtpUseSSL { get; set; }
    public string? EndpointDiscoveryStatus { get; set; }
    public DateTime? EndpointDiscoveryLastCheckedAt { get; set; }
    public DateTime LastSync { get; set; }
    public bool IsEnabled { get; set; } = true;
    
    
    // Folder exclusion functionality
    public string ExcludedFolders { get; set; } = string.Empty;
    
    // Email deletion functionality
    public int? DeleteAfterDays { get; set; }
    
    // Local archive retention functionality
    public int? LocalRetentionDays { get; set; }
    
    // Provider field for account type
    public ProviderType Provider { get; set; } = ProviderType.IMAP;

    // Explicit consumer mail provider identity. Provider remains the legacy
    // transport/import discriminator used by M365 and import-only accounts.
    public MailProviderKind? MailProviderKind { get; set; }
    
    // Microsoft 365 OAuth2 fields (M365 and MSA)
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? TenantId { get; set; }

    // MSA OAuth2 token storage (personal Microsoft accounts)
    public string? OAuthRefreshToken { get; set; }
    public string? OAuthAccessToken { get; set; }
    public DateTime? OAuthTokenExpiry { get; set; }
    public string? OAuthGrantedScopes { get; set; }
    public string? OAuthRedirectUri { get; set; }

    // Credential intake metadata. The raw credential remains encrypted in Password
    // or stored in the existing OAuth fields; these fields describe the last
    // classification/verification result and are safe to expose to the API caller.
    public MailCredentialKind CredentialKind { get; set; } = MailCredentialKind.Unknown;
    public MailCredentialScope CredentialScope { get; set; } = MailCredentialScope.Unknown;
    public string? CredentialDetectionStatus { get; set; }
    public DateTime? CredentialLastCheckedAt { get; set; }

    // Per-account sync scheduling
    public int? SyncIntervalMinutes { get; set; }
    public int? FullSyncIntervalHours { get; set; }
    public DateTime? LastFullSync { get; set; }
    
    [NotMapped]
    public List<string> ExcludedFoldersList
    {
        get
        {
            return string.IsNullOrEmpty(ExcludedFolders) 
                ? new List<string>() 
                : ExcludedFolders.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .Where(f => f.Length > 0)
                    .ToList();
        }
    }
    
    public virtual ICollection<ArchivedEmail> ArchivedEmails { get; set; } = new List<ArchivedEmail>();
    
    // Navigation properties for multi-user functionality
    public virtual ICollection<UserMailAccount> UserMailAccounts { get; set; } = new List<UserMailAccount>();
}
