using System.Security.Claims;

namespace Wisper.Api.Auth;

/// <summary>
/// Validates a Bearer Cognito JWT (issuer, audience, expiry, signature via the pool's
/// JWKS) and maps its claims to a Wisper principal (docs/API.md §2). The interface is
/// deliberately narrow so the real JWKS-backed <see cref="CognitoJwtValidator"/> and a
/// test fake are interchangeable — Grunt has no Cognito, so tests use signed test tokens.
/// </summary>
public interface IJwtValidator
{
    /// <summary>
    /// Validates <paramref name="bearerToken"/> (the raw compact JWT, without the
    /// "Bearer " prefix). Returns success carrying the mapped principal, or a failure
    /// carrying a reason for logging. A null/empty token always fails.
    /// </summary>
    Task<JwtValidationResult> ValidateAsync(string? bearerToken, CancellationToken ct = default);
}

/// <summary>The outcome of <see cref="IJwtValidator.ValidateAsync"/>.</summary>
public sealed class JwtValidationResult
{
    private JwtValidationResult(ClaimsPrincipal? principal, string? failureReason)
    {
        Principal = principal;
        FailureReason = failureReason;
    }

    /// <summary>Whether the token validated.</summary>
    public bool Succeeded => Principal is not null;

    /// <summary>The mapped principal on success; <c>null</c> on failure.</summary>
    public ClaimsPrincipal? Principal { get; }

    /// <summary>A short reason for the failure (for logs), or <c>null</c> on success.</summary>
    public string? FailureReason { get; }

    public static JwtValidationResult Success(ClaimsPrincipal principal) => new(principal, null);

    public static JwtValidationResult Failure(string reason) => new(null, reason);
}
