using System.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Security.Authentication;
using MailArchiver.Models;

namespace MailArchiver.Services;

public interface INetworkHttpClientFactory
{
    HttpClient CreateClient(string name);
}

public sealed class NetworkHttpClientFactory(
    INetworkPolicyResolver resolver,
    ILogger<NetworkHttpClientFactory>? logger = null) : INetworkHttpClientFactory, IHttpClientFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<HttpMessageInvoker>> _connections = new();
    private HttpMessageInvoker Connection(NetworkProxySettings? proxy)
    {
        // Resolve the current device route for each request; never reuse a
        // connection pool belonging to a different proxy or credential set.
        var identity = proxy is null ? "direct" : $"{proxy.Type}|{proxy.Host}|{proxy.Port}|{proxy.Username}|{proxy.Password}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return _connections.GetOrAdd(key, _ => new Lazy<HttpMessageInvoker>(() => new(new SocketsHttpHandler
        {
            UseProxy = proxy is not null, Proxy = proxy is null ? null : RoutedHttpMessageHandler.ToWebProxy(proxy),
            UseCookies = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
        }, disposeHandler: true))).Value;
    }
    public void Dispose()
    {
        foreach (var connection in _connections.Values) if (connection.IsValueCreated) connection.Value.Dispose();
        _connections.Clear();
    }
    public HttpClient CreateClient(string name)
    {
        var handler = new RoutedHttpMessageHandler(resolver, logger, Connection);
        var client = new HttpClient(handler, disposeHandler: true);
        if (name == "MailAutoconfig")
        {
            client.Timeout = TimeSpan.FromSeconds(3);
            client.MaxResponseContentBufferSize = 64 * 1024;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MailAssistant/2.1 autoconfig");
        }
        else
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        }
        return client;
    }

    private sealed class RoutedHttpMessageHandler(
        INetworkPolicyResolver resolver,
        ILogger<NetworkHttpClientFactory>? logger, Func<NetworkProxySettings?, HttpMessageInvoker> connection) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var destination = request.RequestUri ?? throw new InvalidOperationException("HTTP 请求缺少目标地址。");
            var route = resolver.Resolve(destination);
            try
            {
                return await SendOnceAsync(request, route.Proxy, cancellationToken);
            }
            catch (Exception exception) when (route.AllowDirectFallback && !cancellationToken.IsCancellationRequested && IsTransportFailure(exception))
            {
                using var retry = await CloneAsync(request, cancellationToken);
                var response = await SendOnceAsync(retry, proxy: null, cancellationToken);
                response.Headers.TryAddWithoutValidation("X-MailAssistant-Network-Fallback", "direct");
                logger?.LogWarning("Network proxy unavailable for {Host}:{Port}; used device direct connection once", destination.Host, destination.Port);
                return response;
            }
        }

        private Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, NetworkProxySettings? proxy, CancellationToken cancellationToken)
            => connection(proxy).SendAsync(request, cancellationToken);

        internal static WebProxy ToWebProxy(NetworkProxySettings proxy)
        {
            var scheme = proxy.Type == NetworkProxyType.Socks5 ? "socks5" : "http";
            var webProxy = new WebProxy(new Uri($"{scheme}://{proxy.Host}:{proxy.Port}"));
            if (!string.IsNullOrWhiteSpace(proxy.Username))
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? string.Empty);
            return webProxy;
        }

        private static bool IsTransportFailure(Exception exception)
        {
            if (exception is AuthenticationException || exception.InnerException is AuthenticationException)
                return false;
            if (exception.InnerException is not null && IsTransportFailure(exception.InnerException))
                return true;
            return exception is HttpRequestException or IOException or TimeoutException or TaskCanceledException;
        }

        private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                clone.Content = new ByteArrayContent(bytes);
                foreach (var header in request.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return clone;
        }
    }
}
