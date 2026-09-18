using System.Net;
using MailArchiver.Models;
using MailKit.Net.Proxy;

namespace MailArchiver.Services;

public interface INetworkMailProxyFactory
{
    NetworkRoute Apply(MailKit.IMailService client, string host, int port);
    Task<NetworkRoute> ConnectAsync(
        MailKit.IMailService client,
        string host,
        int port,
        Func<CancellationToken, Task> connect,
        CancellationToken cancellationToken = default);
}

public sealed class NetworkMailProxyFactory(
    INetworkPolicyResolver resolver,
    ILogger<NetworkMailProxyFactory>? logger = null) : INetworkMailProxyFactory
{
    public NetworkRoute Apply(MailKit.IMailService client, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(client);
        var destination = new UriBuilder("https", host, port).Uri;
        var route = resolver.Resolve(destination);
        client.ProxyClient = Create(route.Proxy);
        return route;
    }

    public async Task<NetworkRoute> ConnectAsync(
        MailKit.IMailService client,
        string host,
        int port,
        Func<CancellationToken, Task> connect,
        CancellationToken cancellationToken = default)
    {
        var route = Apply(client, host, port);
        try
        {
            await connect(cancellationToken);
            return route;
        }
        catch (Exception exception) when (route.AllowDirectFallback && IsUnavailableProxy(exception))
        {
            client.ProxyClient = null;
            await connect(cancellationToken);
            logger?.LogWarning("Mail proxy unavailable for {Host}:{Port}; used device direct connection once", host, port);
            return route with { Type = NetworkRouteType.Direct, Proxy = null };
        }
    }

    private static bool IsUnavailableProxy(Exception exception)
    {
        if (exception is ProxyProtocolException protocol)
            return protocol.InnerException is System.Net.Sockets.SocketException
                or IOException or TimeoutException;
        return exception is System.Net.Sockets.SocketException
            or IOException
            or TimeoutException;
    }

    internal static IProxyClient? Create(NetworkProxySettings? proxy)
    {
        if (proxy is null)
            return null;
        var credentials = string.IsNullOrWhiteSpace(proxy.Username)
            ? null : new NetworkCredential(proxy.Username, proxy.Password ?? string.Empty);
        return proxy.Type switch
        {
            NetworkProxyType.Socks5 => credentials is null
                ? new Socks5Client(proxy.Host, proxy.Port)
                : new Socks5Client(proxy.Host, proxy.Port, credentials),
            NetworkProxyType.Http => credentials is null
                ? new HttpProxyClient(proxy.Host, proxy.Port)
                : new HttpProxyClient(proxy.Host, proxy.Port, credentials),
            _ => throw new InvalidOperationException("不支持的代理类型。")
        };
    }
}
