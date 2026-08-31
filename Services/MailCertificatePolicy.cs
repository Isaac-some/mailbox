using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace MailArchiver.Services;

public static class MailCertificatePolicy
{
    private const X509ChainStatusFlags UnavailableRevocationFlags =
        X509ChainStatusFlags.RevocationStatusUnknown |
        X509ChainStatusFlags.OfflineRevocation;

    public static bool IsAccepted(SslPolicyErrors errors, X509Chain? chain)
        => errors == SslPolicyErrors.None ||
           IsOnlyUnavailableRevocationCheck(errors, chain?.ChainStatus.Select(status => status.Status));

    internal static bool IsOnlyUnavailableRevocationCheck(
        SslPolicyErrors errors,
        IEnumerable<X509ChainStatusFlags>? statuses)
    {
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || statuses is null)
            return false;

        var hasUnavailableRevocation = false;
        var hasStatus = false;
        foreach (var status in statuses)
        {
            hasStatus = true;
            if ((status & ~UnavailableRevocationFlags) != 0)
                return false;
            if ((status & UnavailableRevocationFlags) != 0)
                hasUnavailableRevocation = true;
        }

        return hasStatus && hasUnavailableRevocation;
    }
}
