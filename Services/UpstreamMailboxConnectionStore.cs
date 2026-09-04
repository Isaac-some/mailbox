using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MailArchiver.Models;

namespace MailArchiver.Services;

public interface IUpstreamMailboxConnectionStore
{
    Task<UpstreamMailboxConnection?> ReadAsync(CancellationToken cancellationToken = default);
    Task<UpstreamMailboxConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string endpoint, string bearerToken, CancellationToken cancellationToken = default);
    Task RemoveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Persists the platform token encrypted by the app's existing credential key.</summary>
public sealed class UpstreamMailboxConnectionStore : IUpstreamMailboxConnectionStore
{
    private const string CredentialEndpointPath = "/api/external/account-credentials";
    private readonly string _path;
    private readonly ICredentialEncryptionService _encryption;
    private readonly SemaphoreSlim _gate = new(1, 1);

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public UpstreamMailboxConnectionStore(IHostEnvironment environment, ICredentialEncryptionService encryption)
        : this(ResolveStorageRoot(environment), encryption)
    {
    }

    public UpstreamMailboxConnectionStore(string storageDirectory, ICredentialEncryptionService encryption)
    {
        _encryption = encryption;
        _path = Path.Combine(storageDirectory, "upstream-mailbox-connection.json");
    }

    public async Task<UpstreamMailboxConnection?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var stored = await ReadStoredAsync(cancellationToken);
        if (stored is null || string.IsNullOrWhiteSpace(stored.Endpoint) || string.IsNullOrWhiteSpace(stored.EncryptedToken))
            return null;

        return new UpstreamMailboxConnection(
            stored.Endpoint,
            _encryption.Decrypt(stored.EncryptedToken),
            stored.InstallationId,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            GetAppVersion());
    }

    public async Task<UpstreamMailboxConnectionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var stored = await ReadStoredAsync(cancellationToken);
        return new UpstreamMailboxConnectionStatus(
            stored is not null && !string.IsNullOrWhiteSpace(stored.Endpoint) && !string.IsNullOrWhiteSpace(stored.EncryptedToken),
            stored?.Endpoint,
            stored?.InstallationId ?? string.Empty,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            GetAppVersion());
    }

    public async Task SaveAsync(string endpoint, string bearerToken, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("平台接口地址必须是完整的 HTTPS 地址。");
        if (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Trim().Length < 20 || bearerToken.Trim().Length > 2048)
            throw new InvalidOperationException("Token 长度必须在 20 到 2048 个字符之间。");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadStoredFileAsync(cancellationToken);
            if (uri.AbsolutePath is "" or "/")
            {
                var builder = new UriBuilder(uri) { Path = CredentialEndpointPath };
                uri = builder.Uri;
            }
            var stored = new StoredConnection
            {
                Endpoint = uri.AbsoluteUri,
                EncryptedToken = _encryption.Encrypt(bearerToken.Trim()),
                InstallationId = Guid.TryParse(existing?.InstallationId, out var id)
                    ? id.ToString("D")
                    : Guid.NewGuid().ToString("D")
            };
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var tempPath = _path + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(stored), cancellationToken);
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoredConnection?> ReadStoredAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadStoredFileAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoredConnection?> ReadStoredFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<StoredConnection>(await File.ReadAllTextAsync(_path, cancellationToken));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("本机平台连接配置已损坏，请移除后重新配置。", ex);
        }
    }

    private static string GetAppVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";

    private static string ResolveStorageRoot(IHostEnvironment environment)
    {
        var dataDirectory = Environment.GetEnvironmentVariable("KOUZI_DATA_DIRECTORY");
        return string.IsNullOrWhiteSpace(dataDirectory) ? environment.ContentRootPath : dataDirectory;
    }

    private sealed class StoredConnection
    {
        public string Endpoint { get; set; } = string.Empty;
        public string EncryptedToken { get; set; } = string.Empty;
        public string InstallationId { get; set; } = string.Empty;
    }
}
