using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Auth;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Auth;

/// <summary>
/// Unit tests for the real <see cref="CognitoJwtValidator"/> using RS256-signed test tokens
/// and a static key provider (no Cognito): issuer/audience/expiry/signature checks and the
/// claims → additive-role mapping (docs/API.md §2).
/// </summary>
public class CognitoJwtValidatorTests
{
    private const string Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_TESTPOOL";
    private const string Audience = "test-app-client-id";

    private static CognitoJwtValidator Build(TestJwtSigner signer, CognitoAuthOptions options)
    {
        var provider = new StaticSigningKeyProvider(signer.PublicKey);
        return new CognitoJwtValidator(
            provider,
            new StaticOptionsMonitor<CognitoAuthOptions>(options),
            NullLogger<CognitoJwtValidator>.Instance);
    }

    private static CognitoAuthOptions Options(bool withAudience = false) => new()
    {
        Issuer = Issuer,
        Audience = withAudience ? new List<string> { Audience } : new List<string>(),
    };

    [Fact]
    public async Task Valid_token_succeeds_and_maps_claims()
    {
        using var signer = new TestJwtSigner();
        var validator = Build(signer, Options());
        var token = signer.CreateToken(Issuer, subject: "sub-1", email: "u@x.com", groups: new[] { "host" });

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal("sub-1", principal.GetSubject());
        Assert.Equal("u@x.com", principal.GetEmail());
        Assert.True(principal.HasRole(WisperRole.Consumer));
        Assert.True(principal.HasRole(WisperRole.Host));
    }

    [Fact]
    public async Task Valid_token_with_matching_audience_succeeds()
    {
        using var signer = new TestJwtSigner();
        var validator = Build(signer, Options(withAudience: true));
        var token = signer.CreateToken(Issuer, audience: Audience);

        var result = await validator.ValidateAsync(token);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Wrong_audience_fails()
    {
        using var signer = new TestJwtSigner();
        var validator = Build(signer, Options(withAudience: true));
        var token = signer.CreateToken(Issuer, audience: "some-other-client");

        var result = await validator.ValidateAsync(token);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Wrong_issuer_fails()
    {
        using var signer = new TestJwtSigner();
        var validator = Build(signer, Options());
        var token = signer.CreateToken("https://evil.example.com/pool");

        var result = await validator.ValidateAsync(token);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Expired_token_fails()
    {
        using var signer = new TestJwtSigner();
        var validator = Build(signer, Options());
        var past = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var token = signer.CreateToken(
            Issuer, notBefore: past, expires: past.AddMinutes(5));

        var result = await validator.ValidateAsync(token);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Signature_from_a_different_key_fails()
    {
        using var signer = new TestJwtSigner("kid-a");
        using var attacker = new TestJwtSigner("kid-b");
        // Validator only trusts `signer`'s public key; token is signed by `attacker`.
        var provider = new StaticSigningKeyProvider(signer.PublicKey);
        var validator = new CognitoJwtValidator(
            provider,
            new StaticOptionsMonitor<CognitoAuthOptions>(Options()),
            NullLogger<CognitoJwtValidator>.Instance);
        var token = attacker.CreateToken(Issuer);

        var result = await validator.ValidateAsync(token);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    public async Task Missing_or_garbage_token_fails(string? token)
    {
        using var signer = new TestJwtSigner();
        var validator = Build(signer, Options());

        var result = await validator.ValidateAsync(token);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task Fails_closed_when_issuer_unconfigured()
    {
        using var signer = new TestJwtSigner();
        var validator = Build(signer, new CognitoAuthOptions());
        var token = signer.CreateToken(Issuer);

        var result = await validator.ValidateAsync(token);

        Assert.False(result.Succeeded);
        Assert.Equal("auth not configured", result.FailureReason);
    }

    [Fact]
    public async Task Fails_when_jwks_unavailable()
    {
        using var signer = new TestJwtSigner();
        var provider = new StaticSigningKeyProvider(signer.PublicKey) { Throw = true };
        var validator = new CognitoJwtValidator(
            provider,
            new StaticOptionsMonitor<CognitoAuthOptions>(Options()),
            NullLogger<CognitoJwtValidator>.Instance);
        var token = signer.CreateToken(Issuer);

        var result = await validator.ValidateAsync(token);

        Assert.False(result.Succeeded);
        Assert.Equal("jwks unavailable", result.FailureReason);
    }
}
