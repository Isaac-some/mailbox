using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailArchiver.Models.Api;

public sealed class ExternalMailboxCredentialsResponse
{
    [JsonPropertyName("data")]
    public ExternalMailboxCredentialsData? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class ExternalMailboxCredentialsData
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("items")]
    public List<ExternalMailboxCredentialItem> Items { get; set; } = new();

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("hasMore")]
    public bool? HasMore { get; set; }

    [JsonPropertyName("serverTime")]
    public string ServerTime { get; set; } = string.Empty;

    // The contract currently returns a Unix number. Keep this as JsonElement so
    // older deployments that serialize it as a string remain readable.
    [JsonPropertyName("serverTimestamp")]
    public JsonElement? ServerTimestamp { get; set; }
}

public sealed class ExternalMailboxCredentialItem
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("credential")]
    public string Credential { get; set; } = string.Empty;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = string.Empty;
}
