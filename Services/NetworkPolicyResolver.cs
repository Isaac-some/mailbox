using MailArchiver.Models;

namespace MailArchiver.Services;

public interface INetworkPolicyResolver
{
    NetworkRoute Resolve(Uri destination);
}

public sealed class NetworkPolicyResolver(INetworkPolicyStore store) : INetworkPolicyResolver
{
    public NetworkRoute Resolve(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var settings = store.GetSnapshot();
        if (settings.Mode == NetworkMode.ExplicitProxy)
            return new NetworkRoute(NetworkRouteType.ExplicitProxy, destination, settings.ExplicitProxy,
                settings.Version, AllowDirectFallback: false);

        var systemProxy = HttpClient.DefaultProxy;
        if (!systemProxy.IsBypassed(destination))
        {
            var proxyUri = systemProxy.GetProxy(destination);
            if (proxyUri is not null && proxyUri != destination)
            {
                var type = proxyUri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase)
                    ? NetworkProxyType.Socks5 : NetworkProxyType.Http;
                return new NetworkRoute(NetworkRouteType.SystemProxy, destination,
                    new NetworkProxySettings(type, proxyUri.Host, proxyUri.Port), settings.Version,
                    AllowDirectFallback: true);
            }
        }

        return new NetworkRoute(NetworkRouteType.Direct, destination, null, settings.Version,
            AllowDirectFallback: false);
    }
}
