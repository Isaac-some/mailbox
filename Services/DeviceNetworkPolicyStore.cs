using MailArchiver.Models;

namespace MailArchiver.Services;

/// <summary>Desktop routing belongs to the device, never to a saved application proxy.</summary>
public sealed class DeviceNetworkPolicyStore : INetworkPolicyStore
{
    public NetworkPolicySettings GetSnapshot() => new(NetworkMode.System);

    public Task SaveAsync(NetworkPolicySettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.Mode != NetworkMode.System)
            throw new InvalidOperationException("应用跟随本机当前网络，请在系统或 VPN 中调整网络连接。");
        return Task.CompletedTask;
    }
}
