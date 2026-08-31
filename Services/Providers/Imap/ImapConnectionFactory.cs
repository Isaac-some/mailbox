using MailArchiver.Models;
using MailArchiver.Services;
using MailArchiver.Services.MailProviders;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Options;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace MailArchiver.Services.Providers.Imap
{
    /// <summary>
    /// Factory for creating, connecting, and authenticating IMAP clients.
    /// Handles SSL/TLS→STARTTLS fallback, SASL PLAIN→auto authentication,
    /// certificate validation, and reconnection logic.
    /// </summary>
    public class ImapConnectionFactory
    {
        private readonly ILogger<ImapConnectionFactory> _logger;
        private readonly MailSyncOptions _mailSyncOptions;
        private readonly BatchOperationOptions _batchOptions;
        private readonly IMailProviderRegistry _mailProviderRegistry;
        private readonly MailProxyOptions _mailProxyOptions;

        public ImapConnectionFactory(
            ILogger<ImapConnectionFactory> logger,
            IOptions<MailSyncOptions> mailSyncOptions,
            IOptions<BatchOperationOptions> batchOptions,
            IMailProviderRegistry mailProviderRegistry,
            IOptions<MailProxyOptions>? mailProxyOptions = null)
        {
            _logger = logger;
            _mailSyncOptions = mailSyncOptions.Value;
            _batchOptions = batchOptions.Value;
            _mailProviderRegistry = mailProviderRegistry;
            _mailProxyOptions = mailProxyOptions?.Value ?? new MailProxyOptions();
        }

        /// <summary>
        /// Creates a new ImapClient instance without protocol logging.
        /// </summary>
        public ImapClient CreateImapClient(string accountName)
        {
            var client = new ImapClient();
            MailProxyClientFactory.Apply(client, _mailProxyOptions);
            return client;
        }

        /// <summary>
        /// Extracts the authentication username from an account.
        /// </summary>
        public static string GetAuthenticationUsername(MailAccount account)
        {
            return account.Username ?? account.EmailAddress;
        }

        /// <summary>
        /// Connects to an IMAP server with SSL/TLS, falling back to STARTTLS if the initial
        /// SSL handshake fails.
        /// </summary>
        public async Task ConnectWithFallbackAsync(ImapClient client, string server, int port, bool useSSL, string accountName)
        {
            if (!useSSL)
            {
                _logger.LogDebug("Connecting to {Server}:{Port} with no security for account {AccountName}",
                    server, port, accountName);
                await client.ConnectAsync(server, port, SecureSocketOptions.None);
                return;
            }

            // First try: SSL/TLS directly
            try
            {
                _logger.LogDebug("Connecting to {Server}:{Port} with SSL/TLS for account {AccountName}",
                    server, port, accountName);
                await client.ConnectAsync(server, port, SecureSocketOptions.SslOnConnect);
                _logger.LogDebug("Successfully connected using SSL/TLS for account {AccountName}", accountName);
            }
            catch (SslHandshakeException sslEx)
            {
                _logger.LogDebug("SSL/TLS connection failed for account {AccountName}, trying STARTTLS: {Message}",
                    accountName, sslEx.Message);

                // Fallback: STARTTLS
                try
                {
                    await client.ConnectAsync(server, port, SecureSocketOptions.StartTls);
                    _logger.LogInformation("Successfully connected using STARTTLS for account {AccountName} on {Server}:{Port}",
                        accountName, server, port);
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(fallbackEx, "STARTTLS fallback also failed for account {AccountName}", accountName);
                    throw new AggregateException("Both SSL/TLS and STARTTLS connection attempts failed", sslEx, fallbackEx);
                }
            }
        }

        /// <summary>
        /// Authenticates the IMAP client. For MSA accounts uses OAuth2 bearer token;
        /// for all other accounts tries SASL PLAIN first, then falls back to auto-negotiation.
        /// </summary>
        public async Task AuthenticateClientAsync(ImapClient client, MailAccount account)
        {
            var module = _mailProviderRegistry.For(account);
            await module.AuthenticateIncomingAsync(client, account);
        }

        public async Task ConnectAccountAsync(ImapClient client, MailAccount account)
        {
            var module = _mailProviderRegistry.For(account);
            var endpoint = module.GetIncomingEndpoint(account);
            client.ServerCertificateValidationCallback = ServerCertificateValidationCallback;
            await ConnectWithFallbackAsync(
                client,
                endpoint.Host,
                endpoint.Port,
                endpoint.UseSsl,
                account.Name);
            await module.AuthenticateIncomingAsync(client, account);
        }

        /// <summary>
        /// Reconnects the IMAP client by disconnecting, delaying, and re-establishing
        /// the connection with authentication.
        /// </summary>
        public async Task ReconnectClientAsync(ImapClient client, MailAccount account)
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.DisconnectAsync(true);
                }

                // Use the configurable pause between batches as reconnection delay
                if (_batchOptions.PauseBetweenBatchesMs > 0)
                {
                    await Task.Delay(_batchOptions.PauseBetweenBatchesMs);
                }

                _logger.LogInformation("Reconnecting to IMAP server for account {AccountName}", account.Name);
                await ConnectAccountAsync(client, account);
                _logger.LogInformation("Successfully reconnected to IMAP server for account {AccountName}", account.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reconnect to IMAP server for account {AccountName}", account.Name);
                throw new InvalidOperationException("Failed to reconnect to IMAP server", ex);
            }
        }

        /// <summary>
        /// Validates the server certificate based on the IgnoreSelfSignedCert setting.
        /// Accepts self-signed certificates and name mismatches when configured to do so.
        /// </summary>
        public bool ServerCertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // A trusted certificate remains acceptable when only the online revocation
            // service is unavailable. Name mismatches and trust-chain errors stay blocked.
            if (MailCertificatePolicy.IsAccepted(sslPolicyErrors, chain))
            {
                return true;
            }

            // If we're configured to ignore self-signed certificates and the only error is
            // that the certificate is untrusted (which is typical for self-signed certs),
            // then accept the certificate
            if (_mailSyncOptions.IgnoreSelfSignedCert &&
                (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors ||
                 sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                // Additional check: if it's a chain error, verify it's specifically a self-signed certificate
                if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateChainErrors && chain.ChainStatus.Length > 0)
                {
                    // Check if the chain status indicates a self-signed certificate
                    bool isSelfSigned = chain.ChainStatus.All(status =>
                        status.Status == X509ChainStatusFlags.UntrustedRoot ||
                        status.Status == X509ChainStatusFlags.PartialChain ||
                        status.Status == X509ChainStatusFlags.RevocationStatusUnknown);

                    if (isSelfSigned)
                    {
                        _logger.LogDebug("Accepting self-signed certificate for IMAP server");
                        return true;
                    }
                }
                else if (sslPolicyErrors == SslPolicyErrors.RemoteCertificateNameMismatch)
                {
                    _logger.LogDebug("Accepting certificate with name mismatch for IMAP server (IgnoreSelfSignedCert=true)");
                    return true;
                }
            }

            // Log the certificate validation error
            _logger.LogWarning("Certificate validation failed for IMAP server: {SslPolicyErrors}", sslPolicyErrors);
            return false;
        }
    }
}
