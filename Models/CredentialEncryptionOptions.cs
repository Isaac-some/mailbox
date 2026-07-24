namespace MailArchiver.Models;

public sealed class CredentialEncryptionOptions
{
    public const string CredentialEncryption = "CredentialEncryption";

    /// <summary>Docker secret path containing a base64-encoded, 32-byte AES key.</summary>
    public string KeyFilePath { get; set; } = "/run/secrets/credential_encryption_key";
}
