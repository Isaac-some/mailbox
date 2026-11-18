using System.Net;
using System.Net.Sockets;
using System.Text;
using MailArchiver.Models;
using MailArchiver.Services;
namespace MailArchiver.Tests.Services;
public sealed class NetworkConnectionReuseTests
{
    [Fact]
    public async Task Consecutive_mail_HTTP_requests_reuse_the_connection_across_client_lifetimes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var accepted = 0; var received = 0;
        var server = Task.Run(async () =>
        {
            while (received < 2)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token); accepted++;
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (received < 2)
                {
                    var line = await reader.ReadLineAsync(timeout.Token); if (line is null) break;
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nOK"), timeout.Token);
                    received++;
                }
            }
        }, timeout.Token);
        try
        {
            var factory = new NetworkHttpClientFactory(new Direct());
            var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/mail");
            for (var i = 0; i < 2; i++) { using var client = factory.CreateClient("Graph"); Assert.Equal("OK", await client.GetStringAsync(uri, timeout.Token)); }
            await server;
            Assert.Equal(1, accepted);
            ((object)factory as IDisposable)?.Dispose();
        }
        finally { timeout.Cancel(); listener.Stop(); }
    }
    private sealed class Direct : INetworkPolicyResolver
    {
        public NetworkRoute Resolve(Uri destination) => new(NetworkRouteType.Direct, destination, null, 1, false);
    }
}
