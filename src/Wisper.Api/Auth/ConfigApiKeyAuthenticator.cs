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
/// only as a dev/bootstrap fallback for a key the DB does not know about (empty, and thus fail-closed, in
/// production). This mirrors <see cref="Tunnel.ConfigHostTokenValidator"/> for the tunnel's host tokens.
/// <para>
/// A matched key's configured subject (<see cref="ApiKeyGrant.UserId"/>, a Cognito <c>sub</c>) must resolve
/// to an <b>active</b> <c>users</c> row. If no row exists yet, the authenticator <b>seeds one on first
/// sight</b> from the grant's <see cref="ApiKeyGrant.Email"/> (an idempotent insert scoped to config-map
/// keys), so a single key drives the whole flow with no out-of-band seeding (task #185). The seed runs in
/// every persistence mode (in-memory and Postgres alike); because <see cref="CognitoAuthOptions.ApiKeys"/>
/// is empty by default outside self-hosted/dev, any key that reaches this branch is operator-controlled and
/// the seed is bounded by that allow-list. A grant with no <see cref="ApiKeyGrant.Email"/> still fails
/// authentication (401), never a downstream 500. A suspended existing owner also fails closed. This
/// mirrors <see cref="DbApiKeyAuthenticator"/>'s owner-must-exist gate so a single mistyped config value
/// cannot take down every authenticated route with an opaque server error.
/// </para>
/// </summary>
public sealed class ConfigApiKeyAuthenticator : IApiKeyAuthenticator
{
    private readonly IOptionsMonitor<CognitoAuthOptions> _options;
    private readonly IUserRepository _users;
    private readonly TimeProvider _time;
    private readonly ILogger<ConfigApiKeyAuthenticator> _logger;

    public ConfigApiKeyAuthenticator(
        IOptionsMonitor<CognitoAuthOptions> options,
        IUserRepository users,
        TimeProvider time,
        ILogger<ConfigApiKeyAuthenticator> logger)
    {
        _options = options;
        _users = users;
        _time = time;
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

        // A config-map key is the bootstrap escape hatch: if the subject has no row yet, seed one from
        // the grant's Email so a fresh boot works end to end (task #185). The seed runs in every
        // persistence mode (in-memory and Postgres alike); Auth:ApiKeys is empty by default outside
        // self-hosted/dev, so any key that reaches this branch is operator-controlled and the seed is
        // bounded by that allow-list. Idempotent (a second key/first request on the same sub finds the
        // row on the initial lookup). Only config-map keys ever take this branch (the DB-key path in
        // DbApiKeyAuthenticator never delegates here for a recognized row; a missing DB owner there
        // still fails closed). A grant with no Email cannot seed a valid row (`users.email` is NOT
        // NULL, docs/DATA_MODEL.md §3), so reject as 401, never 500.
        var user = await ResolveOrBootstrapAsync(matched, matchedRaw, ct);
        if (user is null || user.Status != UserStatus.Active)
        {
            return null;
        }

        return WisperPrincipal.CreateForApiKey(matched.UserId!, matched.Email, matched.Scopes);
    }

    private async Task<User?> ResolveOrBootstrapAsync(
        ApiKeyGrant grant, string? matchedRaw, CancellationToken ct)
    {
        var existing = await _users.GetByCognitoSubAsync(grant.UserId!, ct);
        if (existing is not null)
        {
            if (existing.Status != UserStatus.Active)
            {
                _logger.LogWarning(
                    "Config API key {KeyPrefix} names subject '{Subject}' whose user row is not active; rejecting as 401.",
                    KeyPrefix(matchedRaw), grant.UserId);
            }

            return existing;
        }

        if (string.IsNullOrWhiteSpace(grant.Email))
        {
            _logger.LogWarning(
                "Config API key {KeyPrefix} names subject '{Subject}' that resolves to no user and the grant has " +
                "no Email to bootstrap one; rejecting as 401.",
                KeyPrefix(matchedRaw), grant.UserId);
            return null;
        }

        var now = _time.GetUtcNow();
        var toCreate = new User
        {
            CognitoSub = grant.UserId!,
            Email = grant.Email!,
            Status = UserStatus.Active,
            ConnectStatus = ConnectStatus.None,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            var created = await _users.CreateAsync(toCreate, ct);
            _logger.LogInformation(
                "Config API key {KeyPrefix}: bootstrapped users row for sub '{Subject}' (email {Email}); " +
                "in-memory dev boot seeded from config",
                KeyPrefix(matchedRaw), grant.UserId, grant.Email);
            return created;
        }
        catch (Exception ex)
        {
            // Two callers raced the first bootstrap (or an email collision from another config entry).
            // If a row now exists for this sub it is the winner's; otherwise a real error is worth
            // logging and failing closed on (401).
            var raced = await _users.GetByCognitoSubAsync(grant.UserId!, ct);
            if (raced is not null)
            {
                return raced;
            }

            _logger.LogWarning(
                ex,
                "Config API key {KeyPrefix}: bootstrap for sub '{Subject}' failed and no row now exists; " +
                "rejecting as 401.",
                KeyPrefix(matchedRaw), grant.UserId);
            return null;
        }
    }

    /// <summary>A safe-to-log prefix of the raw key — enough to identify which allow-list entry, not enough to replay it.</summary>
    private static string KeyPrefix(string? raw) =>
        string.IsNullOrEmpty(raw) ? "<unknown>" : raw.Length <= 8 ? raw : raw[..8] + "…";
}
