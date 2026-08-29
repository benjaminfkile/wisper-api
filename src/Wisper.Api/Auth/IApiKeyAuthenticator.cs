using System.Security.Claims;

namespace Wisper.Api.Auth;

/// <summary>
/// Resolves a presented <b>API key</b> (a <c>wck_…</c> Bearer, docs/API.md §2) to a Wisper principal --
/// the API-key analogue of <see cref="IJwtValidator"/>. The production implementation
/// (<see cref="DbApiKeyAuthenticator"/>) does a constant-time hashed lookup against the <c>api_keys</c>
/// table, checks the owning user, and stamps last-used; the config-backed
/// <see cref="ConfigApiKeyAuthenticator"/> is a dev/bootstrap fallback for a DB-less boot. The narrow
/// interface keeps the two interchangeable and lets the auth filter branch on <c>wck_</c> without knowing
/// which store answered.
/// </summary>
public interface IApiKeyAuthenticator
{
    /// <summary>
    /// Resolves <paramref name="bearerToken"/> (the raw key, without the "Bearer " prefix) to the owning
    /// user's principal -- subject = the owner's identity, roles = the key's stored scopes. Returns
    /// <c>null</c> on any fail-closed outcome (null/empty/unknown/revoked key or a suspended/missing owner).
    /// </summary>
    Task<ClaimsPrincipal?> AuthenticateAsync(string? bearerToken, CancellationToken ct = default);
}
