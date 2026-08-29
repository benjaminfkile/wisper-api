using System.Text;
using System.Text.Json;
using Wisper.Api.Accounts;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Infrastructure.Idempotency;
using Wisper.Api.Tunnel;

namespace Wisper.Api.Leases;

/// <summary>
/// The consumer lease surface (docs/API.md §5): <c>POST /v1/leases</c> (create + provision, guarded by a
/// required <c>Idempotency-Key</c>, §9), <c>GET /v1/leases</c> (the caller's leases, status filter +
/// cursor pagination, §10), <c>GET /v1/leases/:id</c> (status + running cost), and
/// <c>DELETE /v1/leases/:id</c> (release, idempotent). All are consumer-gated (every authenticated user
/// is implicitly a consumer, §2) and bootstrap the caller's <c>users</c> row so the lease is owned by a
/// persisted account. This replaces the Phase-1 <c>/dev/leases</c> harness with the authenticated,
/// billed <c>/v1</c> path; the harness stays behind its off-by-default flag.
/// </summary>
public static class LeaseEndpoints
{
    /// <summary>Cap and default for the page size (docs/API.md §10).</summary>
    private const int MaxLimit = 100;
    private const int DefaultLimit = 25;

    /// <summary>The idempotency header money-mutating POSTs require (docs/API.md §9).</summary>
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private const string JsonContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapLeaseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/leases", CreateLeaseAsync).RequireConsumer();
        endpoints.MapGet("/v1/leases", ListLeasesAsync).RequireConsumer();
        endpoints.MapGet("/v1/leases/{id}", GetLeaseAsync).RequireConsumer();
        endpoints.MapDelete("/v1/leases/{id}", ReleaseLeaseAsync).RequireConsumer();
        endpoints.MapPost("/v1/leases/{id}/exec", ExecLeaseAsync).RequireConsumer();
    }

    private static async Task<IResult> CreateLeaseAsync(
        HttpContext http,
        ILeaseService leases,
        IUserAccountService accounts,
        IdempotencyService idempotency,
        CancellationToken ct)
    {
        // The Idempotency-Key is REQUIRED here -- lease creation moves money (the wallet hold), so a retry
        // must never provision twice (docs/API.md §9).
        var key = http.Request.Headers[IdempotencyKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"The {IdempotencyKeyHeader} header is required.",
                new { header = IdempotencyKeyHeader });
        }

        // Read the raw body once: its hash lets a same-key replay (return the stored response) be told
        // apart from a same-key/different-body reuse (409 conflict), docs/API.md §9.
        var body = await ReadBodyAsync(http.Request, ct);
        var request = Deserialize(body);

        // Bootstrap the caller's row so the lease is owned by a persisted account and the idempotency key
        // is scoped to that user (docs/API.md §2, docs/DATA_MODEL.md §10).
        var user = await accounts.BootstrapAsync(http.User, ct);

        var begun = await idempotency.BeginAsync(user.Id, key, RequestHash.Compute(body), ct);
        switch (begun.Outcome)
        {
            case IdempotencyOutcome.Replay:
                return Results.Text(begun.ResponseBody ?? string.Empty, JsonContentType, statusCode: begun.ResponseStatus);
            case IdempotencyOutcome.Conflict:
            case IdempotencyOutcome.InProgress:
                throw new ApiException(ApiErrorCode.Conflict, begun.Message ?? "Idempotency-Key conflict.");
        }

        // We hold the lock. Run the create; store the response so a later retry replays it verbatim. On
        // failure, release the lock so the same key can be retried (the operation didn't complete).
        try
        {
            var result = await leases.CreateAsync(user.Id, request, ct);
            var json = JsonSerializer.Serialize(CreateLeaseResponse.From(result), Json);
            await idempotency.CompleteAsync(key, StatusCodes.Status201Created, json, ct);
            return Results.Text(json, JsonContentType, statusCode: StatusCodes.Status201Created);
        }
        catch
        {
            await idempotency.AbandonAsync(key, CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> ListLeasesAsync(
        HttpContext http, ILeaseService leases, IUserAccountService accounts, CancellationToken ct)
    {
        var query = ParseQuery(http.Request.Query);
        var user = await accounts.BootstrapAsync(http.User, ct);
        var page = await leases.ListAsync(user.Id, query, ct);
        return Results.Json(page);
    }

    private static async Task<IResult> GetLeaseAsync(
        string id, HttpContext http, ILeaseService leases, IUserAccountService accounts, CancellationToken ct)
    {
        var leaseId = RequireLeaseId(id);
        var user = await accounts.BootstrapAsync(http.User, ct);
        var view = await leases.GetAsync(user.Id, leaseId, ct);
        if (view is null)
        {
            throw new ApiException(ApiErrorCode.NotFound, $"No such lease '{id}'.");
        }

        return Results.Json(view);
    }

    private static async Task<IResult> ReleaseLeaseAsync(
        string id, HttpContext http, ILeaseService leases, IUserAccountService accounts, CancellationToken ct)
    {
        var leaseId = RequireLeaseId(id);
        var user = await accounts.BootstrapAsync(http.User, ct);
        var view = await leases.ReleaseAsync(user.Id, leaseId, ct);
        if (view is null)
        {
            throw new ApiException(ApiErrorCode.NotFound, $"No such lease '{id}'.");
        }

        return Results.Json(view);
    }

    /// <summary>
    /// Runs a command in the caller's lease over the tunnel relay (docs/API.md §5, §7). Ownership + ready
    /// state are checked first (404 / 409 <c>lease_not_ready</c> via the envelope) -- before any response
    /// body -- so those errors stay JSON. <c>?stream=1</c> then streams the live output as SSE
    /// (<c>chunk</c>/<c>exit</c>/<c>error</c>); otherwise the buffered <c>{stdout,stderr,exit_code}</c> is
    /// returned. Both drive the same relay exec paths built for the dev harness.
    /// </summary>
    private static async Task<IResult> ExecLeaseAsync(
        string id,
        HttpContext http,
        ILeaseService leases,
        IUserAccountService accounts,
        ITunnelRelay relay,
        CancellationToken ct)
    {
        var leaseId = RequireLeaseId(id);
        var user = await accounts.BootstrapAsync(http.User, ct);

        // Resolve + validate the lease BEFORE touching the response: null → 404, not-active → 409
        // lease_not_ready. Both must reach the client as the uniform error envelope, which the SSE path
        // (a 200 text/event-stream once started) can no longer produce.
        var target = await leases.ResolveExecTargetAsync(user.Id, leaseId, ct);
        if (target is null)
        {
            throw new ApiException(ApiErrorCode.NotFound, $"No such lease '{id}'.");
        }

        var command = await ReadCommandAsync(http.Request, ct);

        // ?stream=1 → streamed exec over SSE (docs/API.md §7); anything else is the sync exec, whose
        // host_offline / upstream_timeout surface from the relay as the uniform error envelope.
        if (http.Request.Query["stream"] == "1")
        {
            await ExecStreamSse.RelayAsync(http, relay, target.HostId, target.LeaseId, command, ct);
            return Results.Empty;
        }

        var result = await relay.ExecAsync(target.HostId, target.LeaseId, command, ct);
        return Results.Json(new ExecResponse(result.Stdout, result.Stderr, result.ExitCode));
    }

    /// <summary>Parses the <c>lease_&lt;guid&gt;</c> route id, 404ing anything malformed (docs/API.md §3).</summary>
    private static Guid RequireLeaseId(string id)
    {
        if (!TunnelLeaseId.TryParse(id, out var leaseId))
        {
            // A malformed id can never name a lease; 404 rather than leak the id shape.
            throw new ApiException(ApiErrorCode.NotFound, $"No such lease '{id}'.");
        }

        return leaseId;
    }

    private static LeaseListQuery ParseQuery(IQueryCollection q) => new()
    {
        Status = ParseStatus(q["status"]),
        Limit = ParseLimit(q["limit"]),
        Cursor = ParseCursor(q["cursor"]),
    };

    private static LeaseStatus? ParseStatus(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!Enum.TryParse<LeaseStatus>(value, ignoreCase: true, out var status) || !Enum.IsDefined(status))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "Unknown 'status' filter.",
                new { field = "status", allowed = Enum.GetValues<LeaseStatus>().Select(PgEnum.ToLabel).ToArray() });
        }

        return status;
    }

    private static int ParseLimit(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return DefaultLimit;
        }

        if (!int.TryParse(value, out var limit) || limit < 1 || limit > MaxLimit)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"'limit' must be an integer between 1 and {MaxLimit}.",
                new { field = "limit", max = MaxLimit });
        }

        return limit;
    }

    private static LeaseCursor? ParseCursor(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!LeaseCursor.TryParse(value, out var cursor))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "'cursor' is malformed.", new { field = "cursor" });
        }

        return cursor;
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Reads the exec command from the request body. An empty body runs an empty command (matching the
    /// tunnel harness); a present-but-malformed body is a <c>validation_error</c> so it flows through the
    /// uniform envelope rather than a framework 400.
    /// </summary>
    private static async Task<string> ReadCommandAsync(HttpRequest request, CancellationToken ct)
    {
        var body = await ReadBodyAsync(request, ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            var exec = JsonSerializer.Deserialize<ExecRequest>(body, Json);
            return exec?.Command ?? string.Empty;
        }
        catch (JsonException)
        {
            throw new ApiException(ApiErrorCode.ValidationError, "The request body is not valid JSON.");
        }
    }

    private static CreateLeaseRequest Deserialize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ApiException(ApiErrorCode.ValidationError, "A request body is required.");
        }

        try
        {
            return JsonSerializer.Deserialize<CreateLeaseRequest>(body, Json)
                ?? throw new ApiException(ApiErrorCode.ValidationError, "A request body is required.");
        }
        catch (JsonException)
        {
            throw new ApiException(ApiErrorCode.ValidationError, "The request body is not valid JSON.");
        }
    }
}
