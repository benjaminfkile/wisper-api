using Wisper.Api.Accounts;
using Wisper.Api.Auth;
using Wisper.Api.Infrastructure;

namespace Wisper.Api.ApiKeys;

/// <summary>
/// The self-serve API-key surface on <c>/v1/me</c> (docs/API.md §5, "API keys"): <c>POST /v1/me/api-keys</c>
/// mints a key (shown once), <c>GET /v1/me/api-keys</c> lists the caller's keys (prefix only), and
/// <c>DELETE /v1/me/api-keys/:id</c> revokes one (idempotent). All three sit behind the <c>consumer</c> gate
/// and bootstrap the caller's <c>users</c> row so keys are scoped to a persisted account. Ownership failures
/// surface as <c>404</c> (§3).
/// <para>
/// <b>Minting requires a JWT principal.</b> A key-authenticated caller is barred from minting more keys
/// (privilege containment, docs/API.md §2) with a clear <c>403</c>, even though its scope otherwise passes
/// the consumer gate. Listing and revoking are fine over either credential.
/// </para>
/// </summary>
public static class ApiKeyEndpoints
{
    public static void MapApiKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/me/api-keys", MintAsync).RequireConsumer();
        endpoints.MapGet("/v1/me/api-keys", ListAsync).RequireConsumer();
        endpoints.MapDelete("/v1/me/api-keys/{id}", RevokeAsync).RequireConsumer();
    }

    private static async Task<IResult> MintAsync(
        MintApiKeyRequest request, HttpContext http, ApiKeyService keys, IUserAccountService accounts,
        CancellationToken ct)
    {
        // Privilege containment (docs/API.md §2): a key must not be able to mint more keys. Minting is
        // JWT-only, so a key-authenticated caller is rejected 403 even though it passed the consumer gate.
        if (http.User.IsApiKeyPrincipal())
        {
            throw new ApiException(
                ApiErrorCode.Forbidden,
                "API keys cannot be minted with an API key; authenticate with a user (JWT) session.");
        }

        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await keys.MintAsync(user.Id, http.User.GetRoles(), request, ct);
        return Results.Json(result, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, ApiKeyService keys, IUserAccountService accounts, CancellationToken ct)
    {
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await keys.ListAsync(user.Id, ct);
        return Results.Json(result);
    }

    private static async Task<IResult> RevokeAsync(
        string id, HttpContext http, ApiKeyService keys, IUserAccountService accounts, CancellationToken ct)
    {
        var keyId = RequireKeyId(id);
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await keys.RevokeAsync(user.Id, keyId, ct);
        return Results.Json(result);
    }

    /// <summary>A non-uuid id can never name a key; <c>404</c> rather than leak the id shape (docs/API.md §3).</summary>
    private static Guid RequireKeyId(string id) =>
        Guid.TryParse(id, out var keyId)
            ? keyId
            : throw new ApiException(ApiErrorCode.NotFound, $"No such API key '{id}'.");
}
