using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Wisper.Api.Auth;

/// <summary>
/// Config-backed <see cref="IApiKeyAuthenticator"/>: the allowed keys, their identities and scopes come
/// from <see cref="CognitoAuthOptions.ApiKeys"/>. Comparison is constant-time
/// (<see cref="CryptographicOperations.FixedTimeEquals"/>). If no keys are configured it <b>fails
/// closed</b> — every key is rejected. It is not the primary authenticator: the DB-backed
/// <see cref="DbApiKeyAuthenticator"/> resolves keys against the <c>api_keys</c> table and delegates here
/// only as a dev/bootstrap fallback for a DB-less boot (empty, and thus fail-closed, in production). This
/// mirrors <see cref="Tunnel.ConfigHostTokenValidator"/> for the tunnel's host tokens.
/// </summary>
public sealed class ConfigApiKeyAuthenticator : IApiKeyAuthenticator
{
    private readonly IOptionsMonitor<CognitoAuthOptions> _options;

    public ConfigApiKeyAuthenticator(IOptionsMonitor<CognitoAuthOptions> options) => _options = options;

    public Task<ClaimsPrincipal?> AuthenticateAsync(string? bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bearerToken))
        {
            return Task.FromResult<ClaimsPrincipal?>(null);
        }

        var keys = _options.CurrentValue.ApiKeys;
        if (keys.Count == 0)
        {
            // Fail closed: an unconfigured allow-list trusts nobody (the production default).
            return Task.FromResult<ClaimsPrincipal?>(null);
        }

        var presented = Encoding.UTF8.GetBytes(bearerToken);

        // Compare against every configured key without short-circuiting, so match latency does not leak
        // which (if any) key matched. FixedTimeEquals is constant-time for equal-length inputs and returns
        // false fast on a length mismatch (length is not secret here).
        ApiKeyGrant? matched = null;
        foreach (var (raw, grant) in keys)
        {
            var candidate = Encoding.UTF8.GetBytes(raw);
            if (CryptographicOperations.FixedTimeEquals(candidate, presented))
            {
                matched = grant;
            }
        }

        if (matched is null || string.IsNullOrWhiteSpace(matched.UserId))
        {
            return Task.FromResult<ClaimsPrincipal?>(null);
        }

        var principal = WisperPrincipal.CreateForApiKey(matched.UserId, email: null, matched.Scopes);
        return Task.FromResult<ClaimsPrincipal?>(principal);
    }
}
