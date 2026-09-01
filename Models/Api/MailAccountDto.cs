namespace MailArchiver.Models.Api;

public class MailAccountDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string? MailProvider { get; set; }
    public string? ImapServer { get; set; }
    public int? ImapPort { get; set; }
    public bool UseSSL { get; set; }
    public string? SmtpServer { get; set; }
    public int? SmtpPort { get; set; }
    public bool? SmtpUseSSL { get; set; }
    public string? EndpointDiscoveryStatus { get; set; }
    public DateTime? EndpointDiscoveryLastCheckedAt { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime LastSync { get; set; }
    public string CredentialKind { get; set; } = string.Empty;
    public string CredentialScope { get; set; } = string.Empty;
    public string CredentialDetectionStatus { get; set; } = string.Empty;
    public DateTime? CredentialLastCheckedAt { get; set; }

    public static MailAccountDto FromEntity(MailAccount a)
    {
        return new MailAccountDto
        {
            Id = a.Id,
            Name = a.Name,
            EmailAddress = a.EmailAddress,
            Provider = a.Provider.ToString(),
            MailProvider = a.MailProviderKind?.ToString(),
            ImapServer = a.ImapServer,
            ImapPort = a.ImapPort,
            UseSSL = a.UseSSL,
            SmtpServer = a.SmtpServer,
            SmtpPort = a.SmtpPort,
            SmtpUseSSL = a.SmtpUseSSL,
            EndpointDiscoveryStatus = a.EndpointDiscoveryStatus,
            EndpointDiscoveryLastCheckedAt = a.EndpointDiscoveryLastCheckedAt,
            IsEnabled = a.IsEnabled,
            LastSync = a.LastSync,
            CredentialKind = a.CredentialKind.ToString(),
            CredentialScope = a.CredentialScope.ToString(),
            CredentialDetectionStatus = a.CredentialDetectionStatus ?? string.Empty,
            CredentialLastCheckedAt = a.CredentialLastCheckedAt
        };
    }
}
