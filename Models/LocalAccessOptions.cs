namespace MailArchiver.Models;

public sealed class LocalAccessOptions
{
    public const string SectionName = "LocalAccess";

    public bool Enabled { get; set; } = true;
    public string ManifestUrl { get; set; } = string.Empty;
    public int RefreshMinutes { get; set; } = 5;
    public List<LocalAccessAccount> Accounts { get; set; } = [];
}

public sealed class LocalAccessAccount
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string PasswordSha256 { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public bool IsActive { get; set; } = true;
}
