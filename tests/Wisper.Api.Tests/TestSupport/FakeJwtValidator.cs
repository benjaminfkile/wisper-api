using System.Security.Claims;
using Wisper.Api.Auth;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A test <see cref="IJwtValidator"/> that returns a preset outcome -- lets the role-gating
/// filter be tested independently of any crypto. By default it authenticates a plain
/// consumer; configure <see cref="Principal"/>/<see cref="Fail"/> to vary the result.
/// </summary>
public sealed class FakeJwtValidator : IJwtValidator
{
    /// <summary>The principal returned on success. Defaults to a plain consumer.</summary>
    public ClaimsPrincipal Principal { get; set; } =
        WisperPrincipal.Create("fake-sub", "fake@example.com", Array.Empty<string>());

    /// <summary>When true, validation fails (as an invalid/missing token would).</summary>
    public bool Fail { get; set; }

    /// <summary>The token last presented to the validator, for assertions.</summary>
    public string? LastToken { get; private set; }

    public Task<JwtValidationResult> ValidateAsync(string? bearerToken, CancellationToken ct = default)
    {
        LastToken = bearerToken;
        var result = Fail || string.IsNullOrWhiteSpace(bearerToken)
            ? JwtValidationResult.Failure("fake failure")
            : JwtValidationResult.Success(Principal);
        return Task.FromResult(result);
    }
}
