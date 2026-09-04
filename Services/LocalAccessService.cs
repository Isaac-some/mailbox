using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailArchiver.Models;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services;

public sealed record LocalAccessResult(bool Succeeded, string Username, bool IsAdmin, string? Error = null);

public interface ILocalAccessService
{
    Task<LocalAccessResult> ValidateAsync(string username, string password, CancellationToken cancellationToken = default);
    Task<bool> IsActiveAsync(string username, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local-app login backed by two configured accounts. A GitHub raw JSON manifest
/// can override the active state without rebuilding the desktop app.
/// </summary>
public sealed class LocalAccessService : ILocalAccessService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LocalAccessOptions _options;
    private readonly ILogger<LocalAccessService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedManifest? _cached;

    public LocalAccessService(
        IHttpClientFactory httpClientFactory,
        IOptions<LocalAccessOptions> options,
        ILogger<LocalAccessService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LocalAccessResult> ValidateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return Denied();

        var manifest = await GetManifestAsync(cancellationToken);
        var account = manifest.FirstOrDefault(candidate =>
            candidate.IsActive && string.Equals(candidate.Username.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase));
        if (account is null || !Matches(account, password))
            return Denied();

        return new LocalAccessResult(true, account.Username.Trim(), account.IsAdmin);
    }

    public async Task<bool> IsActiveAsync(string username, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(username))
            return false;
        var manifest = await GetManifestAsync(cancellationToken);
        return manifest.Any(account => account.IsActive &&
            string.Equals(account.Username.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<LocalAccessAccount>> GetManifestAsync(CancellationToken cancellationToken)
    {
        var refreshAfter = TimeSpan.FromMinutes(Math.Clamp(_options.RefreshMinutes, 1, 60));
        if (_cached is not null && DateTimeOffset.UtcNow - _cached.FetchedAt < refreshAfter)
            return _cached.Accounts;

        if (string.IsNullOrWhiteSpace(_options.ManifestUrl))
            return Cache(_options.Accounts);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cached.FetchedAt < refreshAfter)
                return _cached.Accounts;

            if (!Uri.TryCreate(_options.ManifestUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogError("Local access manifest URL must be an HTTPS URL");
                return CachedOrBundled();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var client = _httpClientFactory.CreateClient("LocalAccessManifest");
            using var response = await client.GetAsync(uri, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Local access manifest returned HTTP {StatusCode}", (int)response.StatusCode);
                return CachedOrBundled();
            }

            var manifest = await response.Content.ReadFromJsonAsync<LocalAccessManifest>(cancellationToken: timeout.Token);
            if (manifest is null || !manifest.Enabled)
                return Cache([]);
            return Cache(ApplyRemoteStates(manifest.Accounts ?? []));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to refresh local access manifest; using bundled local accounts");
            return CachedOrBundled();
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<LocalAccessAccount> Cache(IEnumerable<LocalAccessAccount> accounts)
    {
        var valid = accounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Username))
            .Take(2)
            .ToArray();
        _cached = new CachedManifest(valid, DateTimeOffset.UtcNow);
        return valid;
    }

    private IReadOnlyList<LocalAccessAccount> CachedOrBundled()
        => _cached?.Accounts ?? Cache(_options.Accounts);

    private IEnumerable<LocalAccessAccount> ApplyRemoteStates(IEnumerable<LocalAccessAccount> remoteAccounts)
    {
        var remoteByUsername = remoteAccounts
            .Where(account => !string.IsNullOrWhiteSpace(account.Username))
            .GroupBy(account => account.Username.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var bundled in _options.Accounts)
        {
            if (!remoteByUsername.TryGetValue(bundled.Username.Trim(), out var remote) || !remote.IsActive)
                continue;
            yield return new LocalAccessAccount
            {
                Username = bundled.Username,
                Password = bundled.Password,
                PasswordSha256 = bundled.PasswordSha256,
                IsAdmin = bundled.IsAdmin,
                IsActive = true
            };
        }
    }

    private static bool Matches(LocalAccessAccount account, string password)
    {
        if (!string.IsNullOrEmpty(account.Password))
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(account.Password),
                Encoding.UTF8.GetBytes(password));

        if (string.IsNullOrWhiteSpace(account.PasswordSha256))
            return false;
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual),
            Encoding.UTF8.GetBytes(account.PasswordSha256.Trim().ToUpperInvariant()));
    }

    private static LocalAccessResult Denied() => new(false, string.Empty, false, "账号或密码错误，或账号已停用。");

    private sealed record CachedManifest(IReadOnlyList<LocalAccessAccount> Accounts, DateTimeOffset FetchedAt);

    private sealed class LocalAccessManifest
    {
        public bool Enabled { get; set; } = true;
        public List<LocalAccessAccount>? Accounts { get; set; }
    }
}
