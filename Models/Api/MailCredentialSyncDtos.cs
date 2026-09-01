namespace MailArchiver.Models.Api;

public sealed class MailCredentialSyncRequest
{
    public List<MailCredentialSyncItem> Items { get; set; } = new();
    public bool IsEnabled { get; set; } = true;
}

public sealed class MailCredentialSyncItem
{
    public string Email { get; set; } = string.Empty;
    public string Credential { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string? ClientId { get; set; }
}

public sealed class MailCredentialSyncItemResult
{
    public string Email { get; set; } = string.Empty;
    public bool Created { get; set; }
    public string CredentialKind { get; set; } = string.Empty;
    public string CredentialScope { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
}
