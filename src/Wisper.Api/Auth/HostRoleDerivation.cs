using Wisper.Api.Accounts;
using Wisper.Api.Persistence.Hosts;

namespace Wisper.Api.Auth;

/// <summary>
/// The single source of the become-a-host role signal (docs/API.md §184, §2): whether the authenticated
/// caller owns at least one host. Becoming a host is additive — the moment a consumer registers a host they
/// <b>are</b> a host — so both the <c>host</c> gate (<see cref="WisperAuthFilter"/>) and <c>GET /v1/me</c>
/// derive the <c>host</c> role from this ownership signal, making the new role effective on the caller's
/// <b>current</b> token with no re-login even before the Cognito <c>host</c> group lands on the next token.
/// Routing both callers through one helper keeps them in agreement.
/// <para>
/// The signal is <b>JWT-only</b>: an api-key principal authorizes purely by its explicit stored scopes
/// (docs/API.md §2), never by Cognito groups or ownership, so this helper returns <c>false</c> for one — its
/// host access is scope-driven either way. The answer is resolved <b>at most once per request</b> and cached
/// on <see cref="HttpContext.Items"/>, so multiple host gates in a single request never re-query the repo.
/// </para>
/// </summary>
public static class HostRoleDerivation
{
    /// <summary>
    /// <see cref="HttpContext.Items"/> key under which the per-request ownership answer is cached, so a request
    /// that passes through several host gates (and then the endpoint) resolves ownership only once.
    /// </summary>
    private const string OwnsHostItemKey = "wisper.auth.owns-host";

    /// <summary>
    /// Whether the caller owns ≥1 host — the additive become-a-host signal (docs/API.md §184). Reads
    /// <see cref="HttpContext.User"/>: an api-key principal is <c>false</c> (scopes-only, docs/API.md §2); a JWT
    /// caller resolves to its persisted <c>users</c> row (bootstrapping it if needed) and is <c>true</c> when it
    /// owns any host. Cached on <see cref="HttpContext.Items"/> so it costs a single repo query per request.
    /// </summary>
    public static async ValueTask<bool> OwnsHostAsync(
        HttpContext http, IUserAccountService accounts, IHostRepository hosts, CancellationToken ct = default)
    {
        if (http.Items.TryGetValue(OwnsHostItemKey, out var cached) && cached is bool owns)
        {
            return owns;
        }

        bool result;
        var principal = http.User;
        if (principal.IsApiKeyPrincipal())
        {
            // api-key principals carry explicit scopes by design (docs/API.md §2); never override with ownership.
            result = false;
        }
        else
        {
            var user = await accounts.BootstrapAsync(principal, ct);
            result = (await hosts.ListByOwnerAsync(user.Id, ct)).Count > 0;
        }

        http.Items[OwnsHostItemKey] = result;
        return result;
    }
}
