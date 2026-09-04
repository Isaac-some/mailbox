namespace MailArchiver.Models;

public sealed class PlatformAuthenticationOptions
{
    public const string SectionName = "PlatformAuthentication";

    public string BaseUrl { get; set; } = "https://openrouter.tuchong.com";
    public string LoginPath { get; set; } = "/api/auth/login";
    public string CredentialPath { get; set; } = "/api/external/account-credentials";
    public int TimeoutSeconds { get; set; } = 30;
}
