using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Wisper.Api.Auth;

/// <summary>
/// The real <see cref="IJwtValidator"/>: verifies a Cognito JWT's issuer, audience,
/// lifetime, and RS256 signature against the pool's JWKS keys (docs/API.md §2), then maps
/// the validated claims to a Wisper principal. All parameters are config-driven
/// (<see cref="CognitoAuthOptions"/>); the signing keys come from an
/// <see cref="ISigningKeyProvider"/> so tests exercise this exact path without Cognito.
/// Fails closed: an unconfigured issuer rejects every token.
/// </summary>
public sealed class CognitoJwtValidator : IJwtValidator
{
    private readonly ISigningKeyProvider _keyProvider;
    private readonly IOptionsMonitor<CognitoAuthOptions> _options;
    private readonly ILogger<CognitoJwtValidator> _logger;
    private readonly JsonWebTokenHandler _handler = new();

    public CognitoJwtValidator(
        ISigningKeyProvider keyProvider,
        IOptionsMonitor<CognitoAuthOptions> options,
        ILogger<CognitoJwtValidator> logger)
    {
        _keyProvider = keyProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<JwtValidationResult> ValidateAsync(string? bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            return JwtValidationResult.Failure("missing token");
        }

        var options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            // Fail closed: an unconfigured pool trusts nobody.
            _logger.LogError("auth is not configured (Auth:Issuer is empty); rejecting token");
            return JwtValidationResult.Failure("auth not configured");
        }

        IReadOnlyCollection<SecurityKey> keys;
        try
        {
            keys = await _keyProvider.GetSigningKeysAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "could not obtain JWKS signing keys; rejecting token");
            return JwtValidationResult.Failure("jwks unavailable");
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = options.Audience.Count > 0,
            ValidAudiences = options.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ClockSkew = TimeSpan.FromSeconds(Math.Max(0, options.ClockSkewSeconds)),
            NameClaimType = WisperPrincipal.SubjectClaimType,
            RoleClaimType = WisperPrincipal.GroupsClaimType,
        };

        var result = await _handler.ValidateTokenAsync(bearerToken, parameters);
        if (!result.IsValid)
        {
            var reason = result.Exception?.Message ?? "invalid token";
            _logger.LogWarning(result.Exception, "JWT validation failed: {Reason}", reason);
            return JwtValidationResult.Failure(reason);
        }

        var principal = WisperPrincipal.Create(result.ClaimsIdentity);
        return JwtValidationResult.Success(principal);
    }
}
