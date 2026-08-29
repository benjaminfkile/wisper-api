using System.Text;
using System.Text.Json;
using Wisper.Api.Accounts;
using Wisper.Api.Auth;
using Wisper.Api.Infrastructure;

namespace Wisper.Api.Hosts;

/// <summary>
/// The host registration + pricing surface (docs/API.md §6): <c>POST /v1/hosts</c> (register → agent token
/// once + <c>manager_ws</c>), <c>GET /v1/hosts/mine</c> (owned hosts + presence + earnings),
/// <c>POST /v1/hosts/:id/agent-token</c> (rotate → revoke old + close tunnel 4402), and the priced allow-list
/// (<c>GET/PUT /v1/hosts/:id/images</c>, <c>PATCH /v1/hosts/:id/images/:imageId</c>) validated live against the
/// host's advertised wisp capability. All are host-gated (§6) and bootstrap the caller's <c>users</c> row so
/// hosts are scoped to a persisted account. Ownership failures surface as <c>404</c> (§3). Request bodies are
/// read explicitly (not bound as parameters) so the role gate runs before any content-type match and an
/// omitted body is tolerated where the fields are optional.
/// </summary>
public static class HostEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapHostEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Registering the first host is a CONSUMER action (docs/API.md §184, docs/DESIGN.md §199): becoming a
        // host is additive, so the register call itself only requires the implicit consumer floor and, on
        // success, grants the caller the host group. Every other host endpoint below manages an existing host
        // and correctly stays host-gated -- you must already be a host to touch one.
        endpoints.MapPost("/v1/hosts", RegisterAsync).RequireConsumer();
        // "mine" is a literal segment, so it takes routing precedence over the consumer GET /v1/hosts/{id}.
        endpoints.MapGet("/v1/hosts/mine", ListMineAsync).RequireHost();
        endpoints.MapPost("/v1/hosts/{id}/agent-token", RotateTokenAsync).RequireHost();
        endpoints.MapGet("/v1/hosts/{id}/images", ListImagesAsync).RequireHost();
        endpoints.MapPut("/v1/hosts/{id}/images", ReplaceImagesAsync).RequireHost();
        endpoints.MapPatch("/v1/hosts/{id}/images/{imageId}", PatchImageAsync).RequireHost();
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext http, HostService hosts, IUserAccountService accounts, CancellationToken ct)
    {
        var request = await ReadBodyAsync<RegisterHostRequest>(http.Request, ct)
            ?? new RegisterHostRequest(null, null);
        var user = await accounts.BootstrapAsync(http.User, ct);

        // On success the caller gains the host group (docs/API.md §184). Only a JWT-authenticated caller has a
        // Cognito group to grant; an api-key principal derives its roles from explicit scopes (docs/API.md §2),
        // so we pass no subject to grant for it -- its host access is already scope-driven.
        var grantSub = http.User.IsApiKeyPrincipal() ? null : user.CognitoSub;
        var result = await hosts.RegisterAsync(user.Id, request, grantSub, ct);
        return Results.Json(result, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListMineAsync(
        HttpContext http, HostService hosts, IUserAccountService accounts, CancellationToken ct)
    {
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await hosts.ListMineAsync(user.Id, ct);
        return Results.Json(result);
    }

    private static async Task<IResult> RotateTokenAsync(
        string id, HttpContext http, HostService hosts, IUserAccountService accounts, CancellationToken ct)
    {
        var hostId = RequireHostId(id);
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await hosts.RotateAgentTokenAsync(user.Id, hostId, ct);
        return Results.Json(result);
    }

    private static async Task<IResult> ListImagesAsync(
        string id, HttpContext http, HostService hosts, IUserAccountService accounts, CancellationToken ct)
    {
        var hostId = RequireHostId(id);
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await hosts.ListImagesAsync(user.Id, hostId, ct);
        return Results.Json(result);
    }

    private static async Task<IResult> ReplaceImagesAsync(
        string id, HttpContext http, HostService hosts, IUserAccountService accounts, CancellationToken ct)
    {
        var hostId = RequireHostId(id);
        var request = await ReadBodyAsync<ReplaceImagesRequest>(http.Request, ct)
            ?? new ReplaceImagesRequest(null);
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await hosts.ReplaceImagesAsync(user.Id, hostId, request, ct);
        return Results.Json(result);
    }

    private static async Task<IResult> PatchImageAsync(
        string id, string imageId, HttpContext http, HostService hosts, IUserAccountService accounts,
        CancellationToken ct)
    {
        var hostId = RequireHostId(id);
        if (!Guid.TryParse(imageId, out var image))
        {
            throw new ApiException(ApiErrorCode.NotFound, $"No such image '{imageId}'.");
        }

        var request = await ReadBodyAsync<PatchImageRequest>(http.Request, ct)
            ?? new PatchImageRequest(null, null, null, null, null, null, null);
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await hosts.PatchImageAsync(user.Id, hostId, image, request, ct);
        return Results.Json(result);
    }

    /// <summary>
    /// Reads and deserializes an optional JSON request body. An empty body yields <c>null</c>; a
    /// malformed body is a <c>400 validation_error</c> (docs/API.md §3).
    /// </summary>
    private static async Task<T?> ReadBodyAsync<T>(HttpRequest request, CancellationToken ct)
        where T : class
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (JsonException)
        {
            throw new ApiException(ApiErrorCode.ValidationError, "The request body is not valid JSON.");
        }
    }

    /// <summary>A non-uuid host id can never name a host; <c>404</c> rather than leak the id shape (§3).</summary>
    private static Guid RequireHostId(string id) =>
        Guid.TryParse(id, out var hostId)
            ? hostId
            : throw new ApiException(ApiErrorCode.NotFound, $"No such host '{id}'.");
}
