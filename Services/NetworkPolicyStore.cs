using System.Text.Json;
using MailArchiver.Models;
using Microsoft.AspNetCore.Hosting;

namespace MailArchiver.Services;

public interface INetworkPolicyStore
{
    NetworkPolicySettings GetSnapshot();
    Task SaveAsync(NetworkPolicySettings settings, CancellationToken cancellationToken = default);
}

public sealed class NetworkPolicyStore : INetworkPolicyStore
{
    private sealed record StoredSettings(
        NetworkMode Mode,
        NetworkProxyType? Type,
        string? Host,
        int? Port,
        string? Username,
        string? EncryptedPassword,
        long Version);

    private readonly string _path;
    private readonly ICredentialEncryptionService _encryption;
    private readonly object _gate = new();
    private NetworkPolicySettings? _cached;
    private DateTime _lastWriteUtc;

    public NetworkPolicyStore(IWebHostEnvironment environment, ICredentialEncryptionService encryption)
    {
        _encryption = encryption;
        var root = LocalAppStoragePathPolicy.ResolveWritableRoot(environment.ContentRootPath);
        _path = Path.Combine(root, "network-settings.json");
    }

    public NetworkPolicySettings GetSnapshot()
    {
        lock (_gate)
        {
            var writeTime = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            if (_cached is not null && writeTime == _lastWriteUtc)
                return _cached;

            _cached = Read();
            _lastWriteUtc = writeTime;
            return _cached;
        }
    }

    public async Task SaveAsync(NetworkPolicySettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var proxy = settings.ExplicitProxy;
        var stored = new StoredSettings(
            settings.Mode,
            proxy?.Type,
            proxy?.Host,
            proxy?.Port,
            proxy?.Username,
            string.IsNullOrEmpty(proxy?.Password) ? null : _encryption.Encrypt(proxy.Password),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(stored, new JsonSerializerOptions
        {
            WriteIndented = true
        }), cancellationToken);
        File.Move(temporary, _path, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (PlatformNotSupportedException) { }
        }
        lock (_gate)
        {
            _cached = ToRuntime(stored);
            _lastWriteUtc = File.GetLastWriteTimeUtc(_path);
        }
    }

    private NetworkPolicySettings Read()
    {
        if (!File.Exists(_path))
            return new NetworkPolicySettings();
        try
        {
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_path));
            return stored is null ? new NetworkPolicySettings() : ToRuntime(stored);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("本机网络设置无法读取，请在网络设置中重新保存。", exception);
        }
    }

    private NetworkPolicySettings ToRuntime(StoredSettings stored)
    {
        NetworkProxySettings? proxy = null;
        if (stored.Mode == NetworkMode.ExplicitProxy && stored.Type is not null && stored.Host is not null && stored.Port is not null)
        {
            proxy = new NetworkProxySettings(
                stored.Type.Value,
                stored.Host,
                stored.Port.Value,
                stored.Username,
                string.IsNullOrWhiteSpace(stored.EncryptedPassword) ? null : _encryption.Decrypt(stored.EncryptedPassword));
        }
        var settings = new NetworkPolicySettings(stored.Mode, proxy, stored.Version);
        Validate(settings);
        return settings;
    }

    private static void Validate(NetworkPolicySettings settings)
    {
        if (settings.Mode != NetworkMode.ExplicitProxy)
            return;
        var proxy = settings.ExplicitProxy
            ?? throw new InvalidOperationException("指定代理模式需要填写代理地址。");
        if (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Host.Contains("//", StringComparison.Ordinal))
            throw new InvalidOperationException("代理主机只能填写本机主机名或 IP。");
        if (proxy.Port is < 1 or > 65535)
            throw new InvalidOperationException("代理端口必须在 1 到 65535 之间。");
    }
}
