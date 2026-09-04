namespace MailArchiver.ViewModels;

public sealed class LocalMaintenanceViewModel
{
    public bool PlatformConfigured { get; init; }
    public string PlatformUsername { get; init; } = string.Empty;
    public bool PlatformIsAdmin { get; init; }
    public string PlatformEndpoint { get; init; } = string.Empty;
    public string InstallationId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;
}
