using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// Mints RS256-signed JWTs for tests (Grunt has no Cognito), plus the matching JWKS document
/// and public key — so the real <see cref="Wisper.Api.Auth.CognitoJwtValidator"/> and
/// <see cref="Wisper.Api.Auth.JwksSigningKeyProvider"/> validation paths run end-to-end offline.
/// </summary>
public sealed class TestJwtSigner : IDisposable
{
    // A wide default validity window so tokens validate against the real system clock
    // regardless of the test host's date; the expired-token test overrides these explicitly.
    private static readonly DateTimeOffset DefaultNotBefore = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DefaultExpires = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly RSA _rsa;
    private readonly RsaSecurityKey _signingKey;
    private readonly JsonWebTokenHandler _handler = new();

    public TestJwtSigner(string keyId = "test-key-1")
    {
        _rsa = RSA.Create(2048);
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = keyId };
        KeyId = keyId;
    }

    public string KeyId { get; }

    /// <summary>The public signing key, with a matching <c>kid</c>, for a static key provider.</summary>
    public SecurityKey PublicKey
    {
        get
        {
            var publicOnly = RSA.Create();
            publicOnly.ImportParameters(_rsa.ExportParameters(includePrivateParameters: false));
            return new RsaSecurityKey(publicOnly) { KeyId = KeyId };
        }
    }

    /// <summary>A JWKS document (as Cognito would serve) advertising this signer's public key.</summary>
    public string Jwks()
    {
        var p = _rsa.ExportParameters(includePrivateParameters: false);
        var n = Base64UrlEncoder.Encode(p.Modulus);
        var e = Base64UrlEncoder.Encode(p.Exponent);
        return $$"""
            {"keys":[{"kty":"RSA","use":"sig","kid":"{{KeyId}}","alg":"RS256","n":"{{n}}","e":"{{e}}"}]}
            """;
    }

    /// <summary>Creates a signed token. Defaults produce a valid, unexpired token.</summary>
    public string CreateToken(
        string issuer,
        string subject = "cognito-sub-123",
        string? audience = null,
        string? email = "user@example.com",
        IEnumerable<string>? groups = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (email is not null)
        {
            claims["email"] = email;
        }

        if (groups is not null)
        {
            claims["cognito:groups"] = groups.ToArray();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = null,
            Claims = claims,
            NotBefore = (notBefore ?? DefaultNotBefore).UtcDateTime,
            Expires = (expires ?? DefaultExpires).UtcDateTime,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        };

        return _handler.CreateToken(descriptor);
    }

    public void Dispose() => _rsa.Dispose();
}
