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
    /// set it to require the <c>aud</c> claim (id tokens). Multiple ids are supported
    /// (<c>Auth:Audience:0</c>, <c>Auth:Audience:1</c>, …) so one deployment can accept
    /// id tokens from several app clients — e.g. the consumer/host web client and the
    /// separate admin-panel client that each mint tokens with their own <c>aud</c>.
    /// </summary>
    public IList<string> Audience { get; set; } = new List<string>();

    /// <summary>
    /// The Cognito user-pool id (e.g. <c>us-east-1_ABC123</c>) the runtime writes group membership to when
    /// granting a role — specifically the <c>host</c> group on first host action (docs/API.md §184,
    /// docs/DESIGN.md §199). <b>Unset by default</b>: when absent (in-memory / api-key dev mode, and tests)
    /// the group write degrades to a no-op (<see cref="NoOpUserRoleGranter"/>), so host registration still
    /// succeeds without Cognito. The runtime needs <c>cognito-idp:AdminAddUserToGroup</c> on this pool, and
    /// the pool must have the <c>consumer</c>/<c>host</c>/<c>admin</c> groups provisioned.
    /// </summary>
    public string? UserPoolId { get; set; }

    /// <summary>
    /// The AWS region of <see cref="UserPoolId"/> (e.g. <c>us-east-1</c>), used to construct the Cognito admin
    /// client. <b>Unset by default</b>; when absent (together with <see cref="UserPoolId"/>) the group write is
    /// a no-op. When only one of the two is set the granter still degrades gracefully — both are required to
    /// enable the real Cognito write.
    /// </summary>
    public string? Region { get; set; }

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
    /// On a DB-less bootstrap, the config authenticator seeds a <c>users</c> row for this subject on
    /// first sight from <see cref="Email"/> (idempotent, config-map keys only, task #185), so a fresh
    /// in-memory boot can drive the whole flow with one key without out-of-band seeding. A
    /// pre-existing suspended row still fails authentication with 401 (task #36); a bootstrap that fails
    /// (e.g. the grant has no <see cref="Email"/>) also fails 401, never a downstream 500.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// The email the key authenticates as — mirrors the DB-key path (which carries the owning user's
    /// email). Seeds the principal's <c>email</c> claim so any downstream that displays the caller's email
    /// (e.g. audit rows) sees the same value the DB-key path would. Also seeds a bootstrap <c>users</c>
    /// row (email is <c>NOT NULL</c>, docs/DATA_MODEL.md §3) for this key's <see cref="UserId"/> when no
    /// row exists yet on a DB-less boot (task #185), so a grant without <see cref="Email"/> fails
    /// authentication 401 instead of 500ing downstream. Optional and empty in production, where this
    /// allow-list is inert.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>The granted scopes — the role labels (<c>consumer</c>, <c>host</c>, <c>admin</c>) the key carries.</summary>
    public IList<string> Scopes { get; set; } = new List<string>();
}
