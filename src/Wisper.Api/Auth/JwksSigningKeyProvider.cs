using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Wisper.Api.Auth;

/// <summary>
/// Fetches the Cognito pool's JWKS document over HTTP and caches the parsed signing keys
/// for <see cref="CognitoAuthOptions.JwksCacheMinutes"/> (docs/API.md §2). The cache is
/// refreshed lazily on expiry; a single in-flight refresh is serialized so a burst of
/// requests triggers one fetch. Fails (throws) when auth is unconfigured or the document
/// cannot be retrieved/parsed, so the validator rejects the token rather than trusting it.
/// </summary>
public sealed class JwksSigningKeyProvider : ISigningKeyProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<CognitoAuthOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<JwksSigningKeyProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyCollection<SecurityKey>? _cachedKeys;
    private string? _cachedUri;
    private DateTimeOffset _cacheExpiresAt;

    public JwksSigningKeyProvider(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<CognitoAuthOptions> options,
        TimeProvider clock,
        ILogger<JwksSigningKeyProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(CancellationToken ct = default)
    {
        var options = _options.CurrentValue;
        var uri = options.ResolvedJwksUri
            ?? throw new InvalidOperationException("Auth:Issuer/JwksUri is not configured; cannot fetch JWKS.");

        // Fast path: a fresh cache for the same endpoint.
        if (_cachedKeys is not null && _cachedUri == uri && _clock.GetUtcNow() < _cacheExpiresAt)
        {
            return _cachedKeys;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-check under the lock: another caller may have refreshed while we waited.
            if (_cachedKeys is not null && _cachedUri == uri && _clock.GetUtcNow() < _cacheExpiresAt)
            {
                return _cachedKeys;
            }

            var keys = await FetchAsync(uri, ct);
            _cachedKeys = keys;
            _cachedUri = uri;
            _cacheExpiresAt = _clock.GetUtcNow().AddMinutes(Math.Max(1, options.JwksCacheMinutes));
            _logger.LogInformation("fetched {Count} JWKS signing key(s) from {Uri}", keys.Count, uri);
            return keys;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyCollection<SecurityKey>> FetchAsync(string uri, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        var json = await client.GetStringAsync(uri, ct);
        var keySet = JsonWebKeySet.Create(json);
        var keys = keySet.GetSigningKeys();
        if (keys.Count == 0)
        {
            throw new InvalidOperationException($"JWKS document at {uri} contained no signing keys.");
        }

        return (IReadOnlyCollection<SecurityKey>)keys;
    }
}
