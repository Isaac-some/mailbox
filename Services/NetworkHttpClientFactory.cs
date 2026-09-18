using System.Net;
using System.Security.Authentication;
using MailArchiver.Models;

namespace MailArchiver.Services;

public interface INetworkHttpClientFactory
{
    HttpClient CreateClient(string name);
}

public sealed class NetworkHttpClientFactory(
    INetworkPolicyResolver resolver,
    ILogger<NetworkHttpClientFactory>? logger = null) : INetworkHttpClientFactory, IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var handler = new RoutedHttpMessageHandler(resolver, logger);
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
        ILogger<NetworkHttpClientFactory> logger) : HttpMessageHandler
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

        private static async Task<HttpResponseMessage> SendOnceAsync(
            HttpRequestMessage request,
            NetworkProxySettings? proxy,
            CancellationToken cancellationToken)
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = proxy is not null,
                Proxy = proxy is null ? null : ToWebProxy(proxy),
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };
            using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            return await invoker.SendAsync(request, cancellationToken);
        }

        private static WebProxy ToWebProxy(NetworkProxySettings proxy)
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
