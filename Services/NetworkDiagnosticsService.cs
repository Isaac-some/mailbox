using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using MailArchiver.Models;

namespace MailArchiver.Services;

public interface INetworkDiagnosticsService
{
    Task<NetworkDiagnosticResult> TestHttpsAsync(Uri target, CancellationToken cancellationToken = default);
}

public sealed class NetworkDiagnosticsService(
    INetworkPolicyResolver resolver,
    INetworkHttpClientFactory clients) : INetworkDiagnosticsService
{
    public async Task<NetworkDiagnosticResult> TestHttpsAsync(Uri target, CancellationToken cancellationToken = default)
    {
        var route = resolver.Resolve(target);
        var id = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..6]);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, target);
            using var client = clients.CreateClient("NetworkDiagnostics");
            using var response = await client.SendAsync(request, cancellationToken);
            var fallback = response.Headers.Contains("X-Kouzi-Network-Fallback");
            return new NetworkDiagnosticResult(id, target.Host, target.Port, route.Type,
                NetworkDiagnosticStage.Https, true, (int)response.StatusCode, fallback,
                DateTimeOffset.UtcNow, fallback ? "代理不可用，已临时使用本机直连。" : "HTTPS 连通正常。");
        }
        catch (AuthenticationException)
        {
            return Failed(id, target, route, NetworkDiagnosticStage.Tls, "TLS 验证失败。");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException or TaskCanceledException)
        {
            var stage = route.Type == NetworkRouteType.Direct
                ? NetworkDiagnosticStage.TargetConnect : NetworkDiagnosticStage.ProxyHandshake;
            return Failed(id, target, route, stage, "网络连接失败，请检查当前设备的代理、VPN 或直连网络。");
        }
    }

    private static NetworkDiagnosticResult Failed(
        string id, Uri target, NetworkRoute route, NetworkDiagnosticStage stage, string message)
        => new(id, target.Host, target.Port, route.Type, stage, false, null, false,
            DateTimeOffset.UtcNow, message);
}
