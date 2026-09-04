using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Services;

public sealed class UpstreamMailboxConnectionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mailbox-connection-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Saves_token_encrypted_and_preserves_installation_id_when_updated()
    {
        Directory.CreateDirectory(_directory);
        var keyPath = Path.Combine(_directory, "key");
        await File.WriteAllTextAsync(keyPath, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        var encryption = new CredentialEncryptionService(Options.Create(new CredentialEncryptionOptions { KeyFilePath = keyPath }));
        var store = new UpstreamMailboxConnectionStore(_directory, encryption);

        await store.SaveAsync("https://platform.example/api/external/account-credentials", "first-token-value-1234567890");
        var first = await store.ReadAsync();
        var storedJson = await File.ReadAllTextAsync(Path.Combine(_directory, "upstream-mailbox-connection.json"));

        Assert.NotNull(first);
        Assert.Equal("first-token-value-1234567890", first.BearerToken);
        Assert.True(Guid.TryParse(first.InstallationId, out _));
        Assert.DoesNotContain("first-token-value-1234567890", storedJson, StringComparison.Ordinal);

        await store.SaveAsync("https://platform.example/api/external/account-credentials", "second-token-value-123456789");
        var second = await store.ReadAsync();

        Assert.Equal(first.InstallationId, second!.InstallationId);
        Assert.Equal("second-token-value-123456789", second.BearerToken);
    }

    [Fact]
    public async Task Rejects_non_https_endpoint_and_can_remove_connection()
    {
        Directory.CreateDirectory(_directory);
        var store = new UpstreamMailboxConnectionStore(_directory, new FakeEncryption());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsync("http://platform.example/api/external/account-credentials", "token-value-123456789012345"));

        await store.SaveAsync("https://platform.example/api/external/account-credentials", "token-value-123456789012345");
        Assert.True((await store.GetStatusAsync()).IsConfigured);
        await store.RemoveAsync();
        Assert.False((await store.GetStatusAsync()).IsConfigured);
    }

    [Fact]
    public async Task Appends_platform_credential_path_when_only_domain_is_entered()
    {
        Directory.CreateDirectory(_directory);
        var store = new UpstreamMailboxConnectionStore(_directory, new FakeEncryption());

        await store.SaveAsync("https://platform.example", "token-value-123456789012345");

        Assert.Equal("https://platform.example/api/external/account-credentials", (await store.ReadAsync())!.Endpoint);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeEncryption : ICredentialEncryptionService
    {
        public string Encrypt(string plaintext) => "encrypted:" + plaintext;
        public string Decrypt(string encryptedValue) => encryptedValue[10..];
    }
}
