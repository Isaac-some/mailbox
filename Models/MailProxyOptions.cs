namespace MailArchiver.Models;

public sealed class MailProxyOptions
{
    public const string SectionName = "MailProxy";

    public bool Enabled { get; set; }
    public string Type { get; set; } = "Socks5";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}
