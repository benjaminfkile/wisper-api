namespace Wisper.Api.Auth;

/// <summary>
/// Configuration for Cognito JWT validation (docs/API.md §2), bound from the
/// <see cref="SectionName"/> section. All external inputs are config-driven so the
/// same code validates against the dev and prod pools without recompilation.
/// </summary>
public sealed class CognitoAuthOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Auth";

    /// <summary>
    /// The expected token issuer — the Cognito user-pool URL,
    /// e.g. <c>https://cognito-idp.us-east-1.amazonaws.com/us-east-1_ABC123</c>.
    /// When unset the validator <b>fails closed</b> (every token is rejected).
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// The JWKS document URL used to fetch the pool's signing keys. When unset it is
    /// derived from <see cref="Issuer"/> as <c>{issuer}/.well-known/jwks.json</c>, which
    /// is Cognito's convention.
    /// </summary>
    public string? JwksUri { get; set; }

    /// <summary>
    /// Accepted audiences — the Cognito app-client id(s). When empty, audience is not
    /// validated (Cognito access tokens carry <c>client_id</c> rather than <c>aud</c>);
    /// set it to require the <c>aud</c> claim (id tokens).
    /// </summary>
    public IList<string> Audience { get; set; } = new List<string>();

    /// <summary>Permitted clock skew, in seconds, when checking token lifetime. Default 60.</summary>
    public int ClockSkewSeconds { get; set; } = 60;

    /// <summary>How long fetched JWKS keys are cached before a refresh, in minutes. Default 60.</summary>
    public int JwksCacheMinutes { get; set; } = 60;

    /// <summary>
    /// Dev/bootstrap API-key allow-list (docs/API.md §2), mirroring <c>Tunnel:HostTokens</c>: maps a raw
    /// key string to the identity + scopes it authenticates as. It is the fallback the
    /// <see cref="ConfigApiKeyAuthenticator"/> serves when the DB-backed lookup has no store (a DB-less
    /// boot), so an operator can mint a key locally without Postgres. <b>Empty by default</b>, and thus
    /// <b>fail-closed</b> — production never sets it, so it is inert there.
    /// </summary>
    public Dictionary<string, ApiKeyGrant> ApiKeys { get; set; } = new();

    /// <summary>The effective JWKS URL, resolving the derive-from-issuer default. Null when unconfigured.</summary>
    public string? ResolvedJwksUri =>
        !string.IsNullOrWhiteSpace(JwksUri)
            ? JwksUri
            : string.IsNullOrWhiteSpace(Issuer)
                ? null
                : $"{Issuer.TrimEnd('/')}/.well-known/jwks.json";
}

/// <summary>
/// A single dev/bootstrap API-key grant (docs/API.md §2) — the value side of
/// <see cref="CognitoAuthOptions.ApiKeys"/>. It carries the identity the key authenticates as and the
/// scopes (role labels) it is granted, standing in for a persisted <c>api_keys</c> row on a DB-less boot.
/// </summary>
public sealed class ApiKeyGrant
{
    /// <summary>
    /// The subject the key authenticates as — the identity the resolved principal carries (the same
    /// value the JWT/DB-key paths put in the <c>sub</c> claim, so downstream resolves the same user).
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>The granted scopes — the role labels (<c>consumer</c>, <c>host</c>, <c>admin</c>) the key carries.</summary>
    public IList<string> Scopes { get; set; } = new List<string>();
}
