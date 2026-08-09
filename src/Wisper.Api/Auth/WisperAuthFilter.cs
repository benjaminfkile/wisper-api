using System.Security.Claims;
using Wisper.Api.Accounts;
using Wisper.Api.ApiKeys;
using Wisper.Api.Infrastructure;
using Wisper.Api.Persistence.Hosts;

namespace Wisper.Api.Auth;

/// <summary>
/// The endpoint filter behind the route-group role gates (docs/API.md §2). It authenticates
/// the caller from the <c>Authorization: Bearer</c> header (once per request — a later gate
/// in the same group reuses the resolved principal) and enforces the minimum role.
/// A bearer that looks like an API key (a <c>wck_</c> prefix, <see cref="ApiKeyToken.LooksLikeApiKey"/>)
/// is resolved by the <see cref="IApiKeyAuthenticator"/>; anything else is validated as a Cognito JWT by
/// the <see cref="IJwtValidator"/>. Either way the resulting principal flows through the <b>same</b> role
/// gates below. Unauthenticated/invalid → <c>401 unauthenticated</c>; missing role → <c>403 forbidden</c>,
/// both as the uniform envelope via <see cref="ApiException"/>.
/// <para>
/// The <c>host</c> gate additionally honors DB host-ownership (docs/API.md §184): a JWT caller who owns ≥1
/// host is treated as holding <c>host</c> even if their current token predates the Cognito group add, so
/// becoming a host is effective on the same token with no re-login. This is additive (it never removes a
/// role) and JWT-only — api-key principals authorize purely by their explicit scopes (docs/API.md §2) — and
/// the ownership check runs only for the host gate, at most once per request (see <see cref="HostRoleDerivation"/>).
/// </para>
/// </summary>
public sealed class WisperAuthFilter : IEndpointFilter
{
    private const string BearerPrefix = "Bearer ";

    private readonly WisperRole _requiredRole;

    public WisperAuthFilter(WisperRole requiredRole) => _requiredRole = requiredRole;

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        var principal = http.User;
        if (!IsWisperAuthenticated(principal))
        {
            var token = ReadBearerToken(http.Request.Headers.Authorization.ToString());
            var resolved = await ResolvePrincipalAsync(http, token);
            if (resolved is null)
            {
                throw new ApiException(
                    ApiErrorCode.Unauthenticated, "A valid Bearer token is required.");
            }

            principal = resolved;
            http.User = principal;
        }

        if (!principal.HasRole(_requiredRole) && !await AllowsByHostOwnershipAsync(http, principal))
        {
            throw new ApiException(
                ApiErrorCode.Forbidden,
                $"This action requires the '{WisperRoles.Name(_requiredRole)}' role.");
        }

        return await next(context);
    }

    /// <summary>
    /// Whether the caller passes the gate despite lacking the required role via DB host-ownership
    /// (docs/API.md §184): the <c>host</c> gate treats a JWT caller who owns ≥1 host as holding <c>host</c>, so
    /// becoming a host is effective on the current token with no re-login. Only the host gate consults ownership
    /// — consumer/admin gates return early, adding no DB round-trip — and api-key principals never override
    /// their explicit scopes (docs/API.md §2). The ownership answer is resolved at most once per request
    /// (cached on <see cref="HttpContext.Items"/>, see <see cref="HostRoleDerivation"/>).
    /// </summary>
    private async ValueTask<bool> AllowsByHostOwnershipAsync(HttpContext http, ClaimsPrincipal principal)
    {
        if (_requiredRole != WisperRole.Host || principal.IsApiKeyPrincipal())
        {
            return false;
        }

        var accounts = http.RequestServices.GetRequiredService<IUserAccountService>();
        var hosts = http.RequestServices.GetRequiredService<IHostRepository>();
        return await HostRoleDerivation.OwnsHostAsync(http, accounts, hosts, http.RequestAborted);
    }

    /// <summary>
    /// Resolves the caller's principal from the presented <paramref name="token"/>: a <c>wck_</c> bearer
    /// goes to the API-key authenticator (a hashed lookup, never JWT validation), anything else to the JWT
    /// validator. Returns <c>null</c> when neither recognizes the token, so the caller fails closed 401.
    /// </summary>
    private static async Task<ClaimsPrincipal?> ResolvePrincipalAsync(HttpContext http, string? token)
    {
        if (ApiKeyToken.LooksLikeApiKey(token))
        {
            var authenticator = http.RequestServices.GetRequiredService<IApiKeyAuthenticator>();
            return await authenticator.AuthenticateAsync(token, http.RequestAborted);
        }

        var validator = http.RequestServices.GetRequiredService<IJwtValidator>();
        var result = await validator.ValidateAsync(token, http.RequestAborted);
        return result.Succeeded ? result.Principal : null;
    }

    private static bool IsWisperAuthenticated(ClaimsPrincipal? principal) =>
        principal?.Identity is { IsAuthenticated: true } identity
        && WisperPrincipal.IsWisperAuthenticationType(identity.AuthenticationType);

    private static string? ReadBearerToken(string authorizationHeader)
    {
        if (authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return authorizationHeader[BearerPrefix.Length..].Trim();
        }

        return null;
    }
}
