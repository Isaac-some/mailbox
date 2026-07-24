using System.Security.Cryptography;
using MailArchiver.Models;
using MailArchiver.Services;
using Microsoft.Extensions.Options;

namespace MailArchiver.Tests.Services;

public class CredentialEncryptionServiceTests : IDisposable
{
    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"mail-archiver-test-{Guid.NewGuid():N}.key");

    [Fact]
    public void Encrypt_then_decrypt_returns_the_original_app_password()
    {
        File.WriteAllText(_keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var service = CreateService();

        var encrypted = service.Encrypt("application-password");

        Assert.StartsWith("enc:v1:", encrypted);
        Assert.NotEqual("application-password", encrypted);
        Assert.Equal("application-password", service.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_rejects_an_unencrypted_credential()
    {
        File.WriteAllText(_keyFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var service = CreateService();

        Assert.Throws<InvalidOperationException>(() => service.Decrypt("plain-text-password"));
    }

    public void Dispose()
    {
        if (File.Exists(_keyFile)) File.Delete(_keyFile);
    }

    private CredentialEncryptionService CreateService() => new(
        Options.Create(new CredentialEncryptionOptions { KeyFilePath = _keyFile }));
}
