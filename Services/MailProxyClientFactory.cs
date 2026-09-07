using MailArchiver.Models;
using MailKit.Net.Proxy;

namespace MailArchiver.Services;

// Compatibility shim for callers compiled against v2.0. It is not registered
// in DI and is not read by either desktop launcher.
[Obsolete("Use INetworkMailProxyFactory instead.")]
public static class MailProxyClientFactory
{
    public static IProxyClient? Create(MailProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            return null;
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new InvalidOperationException("邮件代理已启用，但未配置代理主机。");
        if (options.Port is < 1 or > 65535)
            throw new InvalidOperationException("邮件代理端口必须在 1 到 65535 之间。");
        var type = options.Type.ToLowerInvariant() switch
        {
            "http" => NetworkProxyType.Http,
            "socks5" => NetworkProxyType.Socks5,
            _ => throw new InvalidOperationException("邮件代理类型只支持 Socks5 或 Http。")
        };
        return NetworkMailProxyFactory.Create(new NetworkProxySettings(
            type, options.Host, options.Port, options.Username, options.Password));
    }

    public static void Apply(MailKit.IMailService client, MailProxyOptions options)
        => client.ProxyClient = Create(options);
}
