namespace MailArchiver.Models;

public enum NetworkMode
{
    System,
    ExplicitProxy
}

public enum NetworkProxyType
{
    Http,
    Socks5
}

public sealed record NetworkProxySettings(
    NetworkProxyType Type,
    string Host,
    int Port,
    string? Username = null,
    string? Password = null);

public sealed record NetworkPolicySettings(
    NetworkMode Mode = NetworkMode.System,
    NetworkProxySettings? ExplicitProxy = null,
    long Version = 0);

public enum NetworkRouteType
{
    Direct,
    SystemProxy,
    ExplicitProxy
}

public sealed record NetworkRoute(
    NetworkRouteType Type,
    Uri Destination,
    NetworkProxySettings? Proxy,
    long PolicyVersion,
    bool AllowDirectFallback);

public enum NetworkDiagnosticStage
{
    ProxyHandshake,
    TargetConnect,
    Tls,
    ProtocolGreeting,
    Https
}

public sealed record NetworkDiagnosticResult(
    string Id,
    string TargetHost,
    int TargetPort,
    NetworkRouteType RouteType,
    NetworkDiagnosticStage Stage,
    bool Succeeded,
    int? StatusCode,
    bool UsedDirectFallback,
    DateTimeOffset Timestamp,
    string Message);
