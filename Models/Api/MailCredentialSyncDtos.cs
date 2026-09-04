using System.Text.Json.Serialization;

namespace MailArchiver.Models.Api;

public sealed class MailCredentialSyncRequest
{
    [JsonPropertyName("items")]
    public List<MailCredentialSyncItem> Items { get; set; } = new();

    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; set; } = true;
}

public sealed class MailCredentialSyncItem
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("credential")]
    public string Credential { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    [JsonPropertyName("client_id")]
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
