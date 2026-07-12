using Wisper.Api.Accounts;
using Wisper.Api.Auth;

namespace Wisper.Api.Hosts;

/// <summary>
/// The host Connect onboarding surface (docs/API.md §6, docs/PAYMENTS.md §5): <c>POST /v1/hosts/connect</c>
/// (create/continue Stripe Connect Express onboarding → <c>{ onboarding_url }</c>) and
/// <c>GET /v1/hosts/connect/status</c> (<c>connect_status</c> + what's still required). Both sit behind the
/// <c>host</c> gate (§6) and bootstrap the caller's <c>users</c> row so onboarding is scoped to a persisted
/// account. Pricing/earning stays inert until <c>connect_status = 'enabled'</c> (docs/PAYMENTS.md §5).
/// </summary>
public static class HostConnectEndpoints
{
    public static void MapHostConnectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/hosts/connect", StartOnboardingAsync).RequireHost();
        endpoints.MapGet("/v1/hosts/connect/status", GetStatusAsync).RequireHost();
    }

    private static async Task<IResult> StartOnboardingAsync(
        HttpContext http, HostConnectService connect, IUserAccountService accounts, CancellationToken ct)
    {
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await connect.StartOnboardingAsync(user.Id, ct);
        return Results.Json(result);
    }

    private static async Task<IResult> GetStatusAsync(
        HttpContext http, HostConnectService connect, IUserAccountService accounts, CancellationToken ct)
    {
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await connect.GetStatusAsync(user.Id, ct);
        return Results.Json(result);
    }
}
