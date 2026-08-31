using System.Net;
using MailArchiver.Models;
using MailKit.Net.Proxy;

namespace MailArchiver.Services;

public static class MailProxyClientFactory
{
    public static IProxyClient? Create(MailProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            return null;

        var host = options.Host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("邮件代理已启用，但未配置代理主机。");
        if (options.Port is < 1 or > 65535)
            throw new InvalidOperationException("邮件代理端口必须在 1 到 65535 之间。");

        var credentials = string.IsNullOrWhiteSpace(options.Username)
            ? null
            : new NetworkCredential(options.Username, options.Password ?? string.Empty);

        return options.Type.Trim().ToLowerInvariant() switch
        {
            "socks5" => credentials is null
                ? new Socks5Client(host, options.Port)
                : new Socks5Client(host, options.Port, credentials),
            "http" => credentials is null
                ? new HttpProxyClient(host, options.Port)
                : new HttpProxyClient(host, options.Port, credentials),
            _ => throw new InvalidOperationException("邮件代理类型只支持 Socks5 或 Http。")
        };
    }

    public static void Apply(MailKit.IMailService client, MailProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.ProxyClient = Create(options);
    }
}
