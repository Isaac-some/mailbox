namespace MailArchiver.Models;

public enum MailFailureCode
{
    GmailCredentialFormatInvalid,
    AuthenticationRejected,
    ProxyUnavailable,
    TargetUnavailable,
    TlsFailed,
    ProtocolFailed,
    RateLimited,
    Cancelled,
    Unexpected
}

public enum MailFailureStage
{
    CredentialValidation,
    ProxyHandshake,
    TargetConnect,
    Tls,
    ProtocolGreeting,
    Authentication,
    RateLimit,
    Cancelled,
    Unexpected
}

public sealed record MailFailure(
    MailFailureCode Code,
    string Message,
    MailFailureStage Stage,
    string ExceptionCategory);

public sealed class MailCredentialFormatException(string message) : InvalidOperationException(message);

