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

    /// <summary>The effective JWKS URL, resolving the derive-from-issuer default. Null when unconfigured.</summary>
    public string? ResolvedJwksUri =>
        !string.IsNullOrWhiteSpace(JwksUri)
            ? JwksUri
            : string.IsNullOrWhiteSpace(Issuer)
                ? null
                : $"{Issuer.TrimEnd('/')}/.well-known/jwks.json";
}
