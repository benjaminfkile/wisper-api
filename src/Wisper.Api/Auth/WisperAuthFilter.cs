using System.Security.Claims;
using Wisper.Api.Infrastructure;

namespace Wisper.Api.Auth;

/// <summary>
/// The endpoint filter behind the route-group role gates (docs/API.md §2). It authenticates
/// the caller from the <c>Authorization: Bearer</c> header (once per request — a later gate
/// in the same group reuses the resolved principal) and enforces the minimum role.
/// Unauthenticated/invalid → <c>401 unauthenticated</c>; missing role → <c>403 forbidden</c>,
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
            var validator = http.RequestServices.GetRequiredService<IJwtValidator>();
            var token = ReadBearerToken(http.Request.Headers.Authorization.ToString());
            var result = await validator.ValidateAsync(token, http.RequestAborted);
            if (!result.Succeeded)
            {
                throw new ApiException(
                    ApiErrorCode.Unauthenticated, "A valid Bearer token is required.");
            }

            principal = result.Principal!;
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

    private static bool IsWisperAuthenticated(ClaimsPrincipal? principal) =>
        principal?.Identity is { IsAuthenticated: true, AuthenticationType: WisperPrincipal.AuthenticationType };

    private static string? ReadBearerToken(string authorizationHeader)
    {
        if (authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return authorizationHeader[BearerPrefix.Length..].Trim();
        }

        return null;
    }
}
