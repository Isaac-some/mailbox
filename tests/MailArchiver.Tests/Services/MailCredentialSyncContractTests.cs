using System.Text.Json;
using MailArchiver.Models.Api;

namespace MailArchiver.Tests.Services;

public class MailCredentialSyncContractTests
{
    [Fact]
    public void Four_field_contract_uses_stable_snake_case_names()
    {
        var request = new MailCredentialSyncRequest
        {
            IsEnabled = true,
            Items =
            [
                new MailCredentialSyncItem
                {
                    Email = "person@outlook.com",
                    Domain = "outlook.com",
                    Credential = "opaque-value",
                    ClientId = "client-value"
                }
            ]
        };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"is_enabled\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"email\":\"person@outlook.com\"", json, StringComparison.Ordinal);
        Assert.Contains("\"domain\":\"outlook.com\"", json, StringComparison.Ordinal);
        Assert.Contains("\"credential\":\"opaque-value\"", json, StringComparison.Ordinal);
        Assert.Contains("\"client_id\":\"client-value\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_domain_and_client_id_can_be_empty()
    {
        const string json = """
            {
              "is_enabled": true,
              "items": [
                {
                  "email": "person@yahoo.com",
                  "domain": null,
                  "credential": "opaque-value",
                  "client_id": null
                }
              ]
            }
            """;

        var request = JsonSerializer.Deserialize<MailCredentialSyncRequest>(json);

        var item = Assert.Single(request!.Items);
        Assert.Null(item.Domain);
        Assert.Null(item.ClientId);
        Assert.Equal("opaque-value", item.Credential);
    }
}
