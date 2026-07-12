using System.Security.Claims;
using System.Text.Json.Serialization;
using Wisper.Api.Auth;
using Wisper.Api.Domain;

namespace Wisper.Api.Accounts;

/// <summary>
/// The consumer account surface (docs/API.md §5, "Account"): <c>GET /v1/me</c> returns the caller's
/// identity, roles and Connect status, and <c>PATCH /v1/me</c> updates the mutable profile fields. Both
/// sit behind the <c>consumer</c> gate (every authenticated user is implicitly a consumer, §2) and both
/// bootstrap the <c>users</c> row on first sight, so the very first authenticated call a new account ever
/// makes materializes its row (docs/DATA_MODEL.md §3).
/// </summary>
public static class MeEndpoints
{
    public static void MapMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Mapped at the exact path (no route group) so GET/PATCH /v1/me match without a trailing slash.
        endpoints.MapGet("/v1/me", GetMeAsync).RequireConsumer();
        endpoints.MapPatch("/v1/me", PatchMeAsync).RequireConsumer();
    }

    private static async Task<IResult> GetMeAsync(
        HttpContext http, IUserAccountService accounts, CancellationToken ct)
    {
        var user = await accounts.BootstrapAsync(http.User, ct);
        return Results.Json(MeResponse.From(user, http.User));
    }

    private static async Task<IResult> PatchMeAsync(
        MeUpdateRequest request, HttpContext http, IUserAccountService accounts, CancellationToken ct)
    {
        var changes = new ProfileUpdate { Email = request.Email };
        var user = await accounts.UpdateProfileAsync(http.User, changes, ct);
        return Results.Json(MeResponse.From(user, http.User));
    }
}

/// <summary>Body of <c>PATCH /v1/me</c> — the mutable profile fields (all optional).</summary>
public sealed record MeUpdateRequest(
    [property: JsonPropertyName("email")] string? Email);

/// <summary>
/// Wire shape of <c>GET/PATCH /v1/me</c> (docs/API.md §5): identity + additive roles + Connect status.
/// Billing linkage (Stripe ids) is deliberately omitted — it surfaces on the billing endpoints (§5).
/// </summary>
public sealed record MeResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("cognito_sub")] string CognitoSub,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("roles")] IReadOnlyList<string> Roles,
    [property: JsonPropertyName("connect_status")] string ConnectStatus,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt)
{
    /// <summary>Projects a stored <see cref="User"/> plus the caller's roles into the wire shape.</summary>
    public static MeResponse From(User user, ClaimsPrincipal principal) => new(
        Id: user.Id,
        CognitoSub: user.CognitoSub,
        Email: user.Email,
        Status: PgEnum.ToLabel(user.Status),
        Roles: RolesOf(principal),
        ConnectStatus: PgEnum.ToLabel(user.ConnectStatus),
        CreatedAt: user.CreatedAt,
        UpdatedAt: user.UpdatedAt);

    /// <summary>The caller's additive roles in a stable order (consumer → host → admin).</summary>
    private static IReadOnlyList<string> RolesOf(ClaimsPrincipal principal)
    {
        var roles = new List<string>();
        foreach (var role in Enum.GetValues<WisperRole>())
        {
            if (principal.HasRole(role))
            {
                roles.Add(WisperRoles.Name(role));
            }
        }

        return roles;
    }
}
