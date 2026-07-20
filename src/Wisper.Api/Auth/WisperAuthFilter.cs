using System.Security.Claims;
using Wisper.Api.ApiKeys;
using Wisper.Api.Infrastructure;

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

        if (!principal.HasRole(_requiredRole))
        {
            throw new ApiException(
                ApiErrorCode.Forbidden,
                $"This action requires the '{WisperRoles.Name(_requiredRole)}' role.");
        }

        return await next(context);
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
