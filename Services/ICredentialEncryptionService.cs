namespace MailArchiver.Services;

public interface ICredentialEncryptionService
{
    string Encrypt(string plaintext);
    string Decrypt(string encryptedValue);
}
