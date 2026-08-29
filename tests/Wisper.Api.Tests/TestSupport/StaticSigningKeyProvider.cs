using Microsoft.IdentityModel.Tokens;
using Wisper.Api.Auth;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// An <see cref="ISigningKeyProvider"/> backed by a fixed set of keys -- so the real
/// <see cref="Wisper.Api.Auth.CognitoJwtValidator"/> signature/issuer/audience/expiry checks
/// run without a JWKS network round-trip. Configure <see cref="Throw"/> to simulate an
/// unreachable JWKS endpoint.
/// </summary>
public sealed class StaticSigningKeyProvider : ISigningKeyProvider
{
    private readonly IReadOnlyCollection<SecurityKey> _keys;

    public StaticSigningKeyProvider(params SecurityKey[] keys) => _keys = keys;

    /// <summary>When true, <see cref="GetSigningKeysAsync"/> throws (keys unresolvable).</summary>
    public bool Throw { get; set; }

    public Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(CancellationToken ct = default)
    {
        if (Throw)
        {
            throw new InvalidOperationException("jwks unreachable");
        }

        return Task.FromResult(_keys);
    }
}
