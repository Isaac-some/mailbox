namespace MailArchiver.Models;

public sealed record UpstreamMailboxConnection(
    string Endpoint,
    string BearerToken,
    string InstallationId,
    string DeviceName,
    string OperatingSystem,
    string AppVersion);

public sealed record UpstreamMailboxConnectionStatus(
    bool IsConfigured,
    string? Endpoint,
    string InstallationId,
    string DeviceName,
    string OperatingSystem,
    string AppVersion);
