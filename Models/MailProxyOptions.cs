namespace MailArchiver.Models;

// Kept only as a source compatibility shim for older integrations. Production
// routing uses NetworkPolicySettings and never binds this type from configuration.
[Obsolete("Use NetworkPolicySettings instead.")]
public sealed class MailProxyOptions
{
    public bool Enabled { get; set; }
    public string Type { get; set; } = "Socks5";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}
