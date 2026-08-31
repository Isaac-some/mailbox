using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailArchiver.Models;
using MailArchiver.Services;
using MailKit.Net.Proxy;

namespace MailArchiver.Tests.Services;

public class MailTransportPolicyTests
{
    [Fact]
    public void Disabled_proxy_does_not_modify_mail_transport()
        => Assert.Null(MailProxyClientFactory.Create(new MailProxyOptions()));

    [Fact]
    public void Socks5_proxy_uses_the_configured_local_endpoint()
    {
        var proxy = Assert.IsType<Socks5Client>(MailProxyClientFactory.Create(new MailProxyOptions
        {
            Enabled = true,
            Type = "Socks5",
            Host = "127.0.0.1",
            Port = 7897
        }));

        Assert.Equal("127.0.0.1", proxy.ProxyHost);
        Assert.Equal(7897, proxy.ProxyPort);
    }

    [Fact]
    public void Http_proxy_is_supported_without_weakening_mail_server_tls()
        => Assert.IsType<HttpProxyClient>(MailProxyClientFactory.Create(new MailProxyOptions
        {
            Enabled = true,
            Type = "Http",
            Host = "localhost",
            Port = 8080
        }));

    [Theory]
    [InlineData("", 7897)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    public void Enabled_proxy_rejects_invalid_endpoints(string host, int port)
        => Assert.Throws<InvalidOperationException>(() => MailProxyClientFactory.Create(new MailProxyOptions
        {
            Enabled = true,
            Host = host,
            Port = port
        }));

    [Fact]
    public void Certificate_policy_accepts_a_fully_valid_chain()
        => Assert.True(MailCertificatePolicy.IsAccepted(SslPolicyErrors.None, chain: null));

    [Theory]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown)]
    [InlineData(X509ChainStatusFlags.OfflineRevocation)]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)]
    public void Certificate_policy_accepts_only_an_unavailable_revocation_service(
        X509ChainStatusFlags status)
        => Assert.True(MailCertificatePolicy.IsOnlyUnavailableRevocationCheck(
            SslPolicyErrors.RemoteCertificateChainErrors,
            [status]));

    [Theory]
    [InlineData(X509ChainStatusFlags.UntrustedRoot)]
    [InlineData(X509ChainStatusFlags.PartialChain)]
    [InlineData(X509ChainStatusFlags.Revoked)]
    [InlineData(X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.UntrustedRoot)]
    public void Certificate_policy_rejects_real_trust_failures(X509ChainStatusFlags status)
        => Assert.False(MailCertificatePolicy.IsOnlyUnavailableRevocationCheck(
            SslPolicyErrors.RemoteCertificateChainErrors,
            [status]));

    [Fact]
    public void Certificate_policy_rejects_name_mismatches_even_when_revocation_is_unknown()
        => Assert.False(MailCertificatePolicy.IsOnlyUnavailableRevocationCheck(
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            [X509ChainStatusFlags.RevocationStatusUnknown]));
}
