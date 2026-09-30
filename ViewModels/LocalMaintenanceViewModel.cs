using MailArchiver.Models;

namespace MailArchiver.ViewModels;

public sealed class LocalMaintenanceViewModel
{
    public bool PlatformConfigured { get; init; }
    public NetworkMode NetworkMode { get; init; }
    public NetworkProxyType? NetworkProxyType { get; init; }
    public string NetworkProxyHost { get; init; } = string.Empty;
    public int? NetworkProxyPort { get; init; }
    public string NetworkProxyUsername { get; init; } = string.Empty;
}
