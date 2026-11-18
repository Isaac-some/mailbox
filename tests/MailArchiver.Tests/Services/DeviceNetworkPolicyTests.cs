using MailArchiver.Models;
using MailArchiver.Services;

namespace MailArchiver.Tests.Services;

public sealed class DeviceNetworkPolicyTests
{
    [Fact]
    public async Task Desktop_cannot_persist_a_proxy_and_resolver_uses_the_current_device_route()
    {
        var store = new DeviceNetworkPolicyStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new(
            NetworkMode.ExplicitProxy, new(NetworkProxyType.Socks5, "retired-proxy.example", 7890))));
        var route = new NetworkPolicyResolver(store).Resolve(new Uri("https://imap.gmx.com:993"));
        Assert.NotEqual(NetworkRouteType.ExplicitProxy, route.Type);
        Assert.NotEqual("retired-proxy.example", route.Proxy?.Host);
        Assert.Null(store.GetSnapshot().ExplicitProxy);
        Assert.Equal(NetworkMode.System, store.GetSnapshot().Mode);
    }
}
