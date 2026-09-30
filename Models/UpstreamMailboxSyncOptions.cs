namespace MailArchiver.Models;

public sealed class UpstreamMailboxSyncOptions
{
    public const string SectionName = "UpstreamMailboxSync";

    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    /// <summary>Optional server-deployment fallback. Desktop users configure this in the app.</summary>
    public string BearerToken { get; set; } = string.Empty;
    public bool RequireBearerToken { get; set; } = true;
    /// <summary>Optional durable cursor file. Empty uses the local app data directory.</summary>
    public string CursorFilePath { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public int PageSize { get; set; } = 1000;
    public int MaxItems { get; set; } = 100_000;
    public int ServerErrorRetries { get; set; } = 3;
    public int ServerErrorRetryDelaySeconds { get; set; } = 5;
}
