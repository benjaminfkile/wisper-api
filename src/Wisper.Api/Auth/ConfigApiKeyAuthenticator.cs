using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Users;

namespace Wisper.Api.Auth;

/// <summary>
/// Config-backed <see cref="IApiKeyAuthenticator"/>: the allowed keys, their identities and scopes come
/// from <see cref="CognitoAuthOptions.ApiKeys"/>. Comparison is constant-time
/// (<see cref="CryptographicOperations.FixedTimeEquals"/>). If no keys are configured it <b>fails
/// closed</b> — every key is rejected. It is not the primary authenticator: the DB-backed
/// <see cref="DbApiKeyAuthenticator"/> resolves keys against the <c>api_keys</c> table and delegates here
/// only as a dev/bootstrap fallback for a DB-less boot (empty, and thus fail-closed, in production). This
/// mirrors <see cref="Tunnel.ConfigHostTokenValidator"/> for the tunnel's host tokens.
/// <para>
/// A matched key's configured subject (<see cref="ApiKeyGrant.UserId"/>, a Cognito <c>sub</c>) must
/// resolve to an <b>active</b> <c>users</c> row — a misconfigured, unresolvable, or suspended subject
/// <b>fails the authentication</b> (returns null → 401), never a downstream 500. This mirrors
/// <see cref="DbApiKeyAuthenticator"/>'s owner-must-exist gate so a single mistyped config value cannot
/// take down every authenticated route with an opaque server error.
/// </para>
/// </summary>
public sealed class ConfigApiKeyAuthenticator : IApiKeyAuthenticator
{
    private readonly IOptionsMonitor<CognitoAuthOptions> _options;
    private readonly IUserRepository _users;
    private readonly ILogger<ConfigApiKeyAuthenticator> _logger;

    public ConfigApiKeyAuthenticator(
        IOptionsMonitor<CognitoAuthOptions> options,
        IUserRepository users,
        ILogger<ConfigApiKeyAuthenticator> logger)
    {
        _options = options;
        _users = users;
        _logger = logger;
    }

    public async Task<ClaimsPrincipal?> AuthenticateAsync(string? bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(bearerToken))
        {
            return null;
        }

        var keys = _options.CurrentValue.ApiKeys;
        if (keys.Count == 0)
        {
            // Fail closed: an unconfigured allow-list trusts nobody (the production default).
            return null;
        }

        var presented = Encoding.UTF8.GetBytes(bearerToken);

        // Compare against every configured key without short-circuiting, so match latency does not leak
        // which (if any) key matched. FixedTimeEquals is constant-time for equal-length inputs and returns
        // false fast on a length mismatch (length is not secret here).
        ApiKeyGrant? matched = null;
        string? matchedRaw = null;
        foreach (var (raw, grant) in keys)
        {
            var candidate = Encoding.UTF8.GetBytes(raw);
            if (CryptographicOperations.FixedTimeEquals(candidate, presented))
            {
                matched = grant;
                matchedRaw = raw;
            }
        }

        if (matched is null || string.IsNullOrWhiteSpace(matched.UserId))
        {
            return null;
        }

        // The configured subject must map to an active user; a mistyped or stale sub is an auth failure
        // (401), not a 500 out of downstream user resolution.
        var user = await _users.GetByCognitoSubAsync(matched.UserId, ct);
        if (user is null || user.Status != UserStatus.Active)
        {
            _logger.LogWarning(
                "Config API key {KeyPrefix} names subject '{Subject}' that resolves to no active user; rejecting as 401.",
                KeyPrefix(matchedRaw), matched.UserId);
            return null;
        }

        return WisperPrincipal.CreateForApiKey(matched.UserId, matched.Email, matched.Scopes);
    }

    /// <summary>A safe-to-log prefix of the raw key — enough to identify which allow-list entry, not enough to replay it.</summary>
    private static string KeyPrefix(string? raw) =>
        string.IsNullOrEmpty(raw) ? "<unknown>" : raw.Length <= 8 ? raw : raw[..8] + "…";
}
