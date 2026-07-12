using Microsoft.IdentityModel.Tokens;

namespace Wisper.Api.Auth;

/// <summary>
/// Supplies the signing keys used to verify JWT signatures. The real
/// <see cref="JwksSigningKeyProvider"/> fetches (and caches) the Cognito pool's JWKS
/// document; tests supply a static key so the validation path is exercised without a
/// network round-trip.
/// </summary>
public interface ISigningKeyProvider
{
    /// <summary>
    /// Returns the current set of signing keys. Implementations may cache; a throw means
    /// the keys could not be resolved and the caller should treat the token as unverifiable.
    /// </summary>
    Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(CancellationToken ct = default);
}
