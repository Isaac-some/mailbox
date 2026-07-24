using System.Security.Cryptography;
using System.Text;
using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services;

/// <summary>
/// Encrypts provider credentials before they are persisted. The key never enters
/// application configuration: Docker mounts it as a read-only secret file.
/// </summary>
public sealed class CredentialEncryptionService : ICredentialEncryptionService
{
    private const string Prefix = "enc:v1:";
    private readonly byte[] _key;

    public CredentialEncryptionService(IOptions<CredentialEncryptionOptions> options)
    {
        var path = options.Value.KeyFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException("Credential encryption key file is missing.");
        }

        try
        {
            _key = Convert.FromBase64String(File.ReadAllText(path).Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Credential encryption key must be base64 encoded.", ex);
        }

        if (_key.Length != 32)
        {
            throw new InvalidOperationException("Credential encryption key must contain exactly 32 bytes.");
        }
    }

    public string Encrypt(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        return Prefix + Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
    }

    public string Decrypt(string encryptedValue)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue) || !encryptedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A plaintext provider credential was rejected by the read-only archive.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(encryptedValue[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Stored credential has an invalid encrypted format.", ex);
        }

        if (payload.Length < 28)
        {
            throw new InvalidOperationException("Stored credential has an invalid encrypted payload.");
        }

        var nonce = payload[..12];
        var tag = payload[12..28];
        var ciphertext = payload[28..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, tagSizeInBytes: 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
