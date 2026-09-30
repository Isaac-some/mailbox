namespace MailArchiver.Models;

public sealed class PlatformAuthenticationOptions
{
    public const string SectionName = "PlatformAuthentication";

    public string BaseUrl { get; set; } = "https://openrouter.tuchong.com";
    public string LoginPath { get; set; } = "/api/auth/login";
    public string CredentialPath { get; set; } = "/api/external/account-credentials";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;

    public bool HasFixedCredentials
        => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}
