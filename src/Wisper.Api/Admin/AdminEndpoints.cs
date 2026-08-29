using System.Text;
using System.Text.Json;
using Wisper.Api.Accounts;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Infrastructure.Idempotency;
using Wisper.Api.Leases;
using Wisper.Api.Persistence.Audit;

namespace Wisper.Api.Admin;

/// <summary>
/// The admin API surface (docs/API.md §8), all admin-group-gated (<see cref="WisperAuthExtensions.RequireAdmin"/>):
/// <c>GET /overview</c>; <c>GET/PUT /policy</c> (versioned, audited); <c>GET /hosts</c> · <c>/users</c> (search);
/// <c>POST /hosts/:id/suspend|unsuspend</c> · <c>/users/:id/suspend|unsuspend</c> (moderation, audited);
/// <c>POST /refunds</c> · <c>/adjustments</c> (<c>Idempotency-Key</c> required, audited); <c>GET /audit</c>; and
/// <c>GET /ledger/accounts/:id</c> (read-only forensics). Every admin write records an <c>audit_log</c> row with
/// the acting admin (bootstrapped from the JWT) + before/after (docs/DATA_MODEL.md §12).
/// </summary>
public static class AdminEndpoints
{
    /// <summary>The idempotency header the money-mutating admin writes require (docs/API.md §8, §9).</summary>
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private const string JsonContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/v1/admin").RequireAdmin();

        admin.MapGet("/overview", GetOverviewAsync);
        admin.MapGet("/policy", GetPolicyAsync);
        admin.MapPut("/policy", PublishPolicyAsync);
        admin.MapGet("/hosts", SearchHostsAsync);
        admin.MapGet("/users", SearchUsersAsync);
        admin.MapPost("/hosts/{id}/suspend", SuspendHostAsync);
        admin.MapPost("/hosts/{id}/unsuspend", UnsuspendHostAsync);
        admin.MapPost("/users/{id}/suspend", SuspendUserAsync);
        admin.MapPost("/users/{id}/unsuspend", UnsuspendUserAsync);
        admin.MapPost("/refunds", RefundAsync);
        admin.MapPost("/adjustments", AdjustAsync);
        admin.MapGet("/audit", GetAuditAsync);
        admin.MapGet("/ledger/accounts", ListLedgerAccountsAsync);
        admin.MapGet("/ledger/accounts/{id}", GetLedgerAccountAsync);
        admin.MapGet("/leases", ListLeasesAsync);
        admin.MapPost("/leases/{id}/end", ForceEndLeaseAsync);
    }

    private static async Task<IResult> GetOverviewAsync(
        HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct)
    {
        await accounts.BootstrapAsync(http.User, ct);
        return Results.Json(await admin.GetOverviewAsync(ct));
    }

    private static async Task<IResult> GetPolicyAsync(
        HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct)
    {
        await accounts.BootstrapAsync(http.User, ct);
        return Results.Json(await admin.GetPolicyAsync(ct));
    }

    private static async Task<IResult> PublishPolicyAsync(
        HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct)
    {
        var request = Deserialize<PolicyUpdateRequest>(await ReadBodyAsync(http.Request, ct));
        var actor = await accounts.BootstrapAsync(http.User, ct);
        return Results.Json(await admin.PublishPolicyAsync(actor.Id, request, ct));
    }

    private static async Task<IResult> SearchHostsAsync(
        HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct)
    {
        await accounts.BootstrapAsync(http.User, ct);
        var (limit, offset) = ParsePaging(http.Request.Query);
        var query = http.Request.Query["query"].ToString();
        return Results.Json(await admin.SearchHostsAsync(NullIfBlank(query), limit, offset, ct));
    }

    private static async Task<IResult> SearchUsersAsync(
        HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct)
    {
        await accounts.BootstrapAsync(http.User, ct);
        var (limit, offset) = ParsePaging(http.Request.Query);
        var query = http.Request.Query["query"].ToString();
        return Results.Json(await admin.SearchUsersAsync(NullIfBlank(query), limit, offset, ct));
    }

    private static Task<IResult> SuspendHostAsync(
        string id, HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct) =>
        ModerateHostAsync(id, suspend: true, http, admin, accounts, ct);

    private static Task<IResult> UnsuspendHostAsync(
        string id, HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct) =>
        ModerateHostAsync(id, suspend: false, http, admin, accounts, ct);

    private static Task<IResult> SuspendUserAsync(
        string id, HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct) =>
        ModerateUserAsync(id, suspend: true, http, admin, accounts, ct);

    private static Task<IResult> UnsuspendUserAsync(
        string id, HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct) =>
        ModerateUserAsync(id, suspend: false, http, admin, accounts, ct);

    private static async Task<IResult> ModerateHostAsync(
        string id, bool suspend, HttpContext http, AdminService admin, IUserAccountService accounts,
        CancellationToken ct)
    {
        var hostId = RequireId(id, "host");
        var actor = await accounts.BootstrapAsync(http.User, ct);
        return Results.Json(await admin.SetHostSuspendedAsync(actor.Id, hostId, suspend, ct));
    }

    private static async Task<IResult> ModerateUserAsync(
        string id, bool suspend, HttpContext http, AdminService admin, IUserAccountService accounts,
        CancellationToken ct)
    {
        var userId = RequireId(id, "user");
        var actor = await accounts.BootstrapAsync(http.User, ct);
        return Results.Json(await admin.SetUserSuspendedAsync(actor.Id, userId, suspend, ct));
    }

    private static async Task<IResult> RefundAsync(
        HttpContext http,
        AdminService admin,
        IUserAccountService accounts,
        IdempotencyService idempotency,
        CancellationToken ct)
    {
        var key = RequireIdempotencyKey(http);
        var body = await ReadBodyAsync(http.Request, ct);
        var request = Deserialize<AdminRefundRequest>(body);
        var actor = await accounts.BootstrapAsync(http.User, ct);

        return await WithIdempotencyAsync(idempotency, actor.Id, key, body,
            () => admin.RefundAsync(actor.Id, request, key, ct));
    }

    private static async Task<IResult> AdjustAsync(
        HttpContext http,
        AdminService admin,
        IUserAccountService accounts,
        IdempotencyService idempotency,
        CancellationToken ct)
    {
        var key = RequireIdempotencyKey(http);
        var body = await ReadBodyAsync(http.Request, ct);
        var request = Deserialize<AdjustmentRequest>(body);
        var actor = await accounts.BootstrapAsync(http.User, ct);

        return await WithIdempotencyAsync(idempotency, actor.Id, key, body,
            () => admin.AdjustAsync(actor.Id, request, key, ct));
    }

    private static async Task<IResult> GetAuditAsync(
        HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct)
    {
        await accounts.BootstrapAsync(http.User, ct);

        var q = http.Request.Query;
        var limit = ParseLimit(q["limit"], AdminService.MaxAuditLimit, 50);
        long? beforeId = null;
        if (!string.IsNullOrEmpty(q["cursor"]))
        {
            if (!AuditCursor.TryParse(q["cursor"], out var parsed))
            {
                throw new ApiException(
                    ApiErrorCode.ValidationError, "'cursor' is malformed.", new { field = "cursor" });
            }

            beforeId = parsed;
        }

        var query = new AuditLogQuery
        {
            ActorUserId = ParseGuid(q["actor"], "actor"),
            TargetType = NullIfBlank(q["target_type"].ToString()),
            TargetId = ParseGuid(q["target_id"], "target_id"),
            Action = NullIfBlank(q["action"].ToString()),
            BeforeId = beforeId,
            // Over-fetch one to learn whether another page exists.
            Limit = limit + 1,
        };

        var rows = await admin.QueryAuditAsync(query, ct);
        var more = rows.Count > limit;
        var page = (more ? rows.Take(limit) : rows).Select(ToAuditView).ToList();
        var nextCursor = more && page.Count > 0 ? AuditCursor.Encode(page[^1].Id) : null;
        return Results.Json(new AuditPage(page, nextCursor));
    }

    private static async Task<IResult> GetLedgerAccountAsync(
        string id, HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct)
    {
        var accountId = RequireId(id, "ledger account");
        await accounts.BootstrapAsync(http.User, ct);
        return Results.Json(await admin.GetLedgerAccountAsync(accountId, ct));
    }

    private static async Task<IResult> ListLedgerAccountsAsync(
        HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct)
    {
        await accounts.BootstrapAsync(http.User, ct);
        var (limit, offset) = ParsePaging(http.Request.Query);
        var kind = ParseLedgerAccountKind(http.Request.Query["kind"].ToString());
        var ownerUserId = ParseGuid(http.Request.Query["owner_user_id"], "owner_user_id");
        return Results.Json(await admin.ListLedgerAccountsAsync(kind, ownerUserId, limit, offset, ct));
    }

    private static async Task<IResult> ListLeasesAsync(
        HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct)
    {
        await accounts.BootstrapAsync(http.User, ct);
        var (limit, offset) = ParsePaging(http.Request.Query);
        var status = ParseLeaseStatus(http.Request.Query["status"].ToString());
        var pastTtl = ParseBool(http.Request.Query["past_ttl"].ToString(), "past_ttl");
        return Results.Json(await admin.ListLeasesAsync(status, pastTtl, limit, offset, ct));
    }

    private static async Task<IResult> ForceEndLeaseAsync(
        string id, HttpContext http, AdminService admin, IUserAccountService accounts, CancellationToken ct)
    {
        var leaseId = ParseLeaseId(id);
        var actor = await accounts.BootstrapAsync(http.User, ct);
        return Results.Json(await admin.ForceEndLeaseAsync(actor.Id, leaseId, ct));
    }

    /// <summary>
    /// Wraps a money-mutating admin write in the <c>Idempotency-Key</c> replay/conflict/lock protocol
    /// (docs/API.md §9): replays a stored response, 409s a conflicting/in-flight reuse, else runs the
    /// operation and stores the response; a failure releases the lock so the same key can be retried.
    /// </summary>
    private static async Task<IResult> WithIdempotencyAsync<T>(
        IdempotencyService idempotency, Guid actorId, string key, string body, Func<Task<T>> operation)
    {
        var begun = await idempotency.BeginAsync(actorId, key, RequestHash.Compute(body));
        switch (begun.Outcome)
        {
            case IdempotencyOutcome.Replay:
                return Results.Text(begun.ResponseBody ?? string.Empty, JsonContentType, statusCode: begun.ResponseStatus);
            case IdempotencyOutcome.Conflict:
            case IdempotencyOutcome.InProgress:
                throw new ApiException(ApiErrorCode.Conflict, begun.Message ?? "Idempotency-Key conflict.");
        }

        try
        {
            var result = await operation();
            var json = JsonSerializer.Serialize(result, Json);
            await idempotency.CompleteAsync(key, StatusCodes.Status200OK, json);
            return Results.Text(json, JsonContentType, statusCode: StatusCodes.Status200OK);
        }
        catch
        {
            await idempotency.AbandonAsync(key, CancellationToken.None);
            throw;
        }
    }

    private static AuditView ToAuditView(AuditLogEntry entry)
    {
        JsonElement? meta = null;
        if (!string.IsNullOrWhiteSpace(entry.Meta))
        {
            try
            {
                using var doc = JsonDocument.Parse(entry.Meta);
                meta = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                meta = null; // A non-JSON meta (shouldn't happen -- the column is jsonb) is omitted, not fatal.
            }
        }

        return new AuditView(
            entry.Id, entry.ActorUserId, entry.Action, entry.TargetType, entry.TargetId, meta, entry.CreatedAt);
    }

    private static string RequireIdempotencyKey(HttpContext http)
    {
        var key = http.Request.Headers[IdempotencyKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"The {IdempotencyKeyHeader} header is required.",
                new { header = IdempotencyKeyHeader });
        }

        return key;
    }

    private static (int Limit, int Offset) ParsePaging(IQueryCollection query)
    {
        var limit = ParseLimit(query["limit"], AdminService.MaxLimit, AdminService.DefaultLimit);
        var offset = 0;
        var raw = query["offset"].ToString();
        if (!string.IsNullOrEmpty(raw) && (!int.TryParse(raw, out offset) || offset < 0))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "'offset' must be a non-negative integer.", new { field = "offset" });
        }

        return (limit, offset);
    }

    private static int ParseLimit(string? value, int max, int fallback)
    {
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, out var limit) || limit < 1 || limit > max)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"'limit' must be an integer between 1 and {max}.",
                new { field = "limit", max });
        }

        return limit;
    }

    private static Guid? ParseGuid(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Guid.TryParse(value, out var id))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, $"'{field}' must be a uuid.", new { field });
        }

        return id;
    }

    /// <summary>A non-uuid id can never name a row; <c>404</c> rather than leak the id shape (docs/API.md §3).</summary>
    private static Guid RequireId(string id, string what) =>
        Guid.TryParse(id, out var parsed)
            ? parsed
            : throw new ApiException(ApiErrorCode.NotFound, $"No such {what} '{id}'.");

    /// <summary>
    /// Parses an admin lease id -- the same <c>lease_&lt;guid&gt;</c> token the consumer surface accepts --
    /// AND falls back to a plain uuid so an operator pasting either shape can end the lease. A non-parseable
    /// id is <c>404</c> so the id shape is never leaked (docs/API.md §3).
    /// </summary>
    private static Guid ParseLeaseId(string id)
    {
        if (TunnelLeaseId.TryParse(id, out var fromToken))
        {
            return fromToken;
        }

        if (Guid.TryParse(id, out var parsed))
        {
            return parsed;
        }

        throw new ApiException(ApiErrorCode.NotFound, $"No such lease '{id}'.");
    }

    /// <summary>
    /// Parses the optional <c>status</c> query for the admin lease listing (task #57): a blank value means
    /// "any non-terminal status", and any other value is validated against <see cref="LeaseStatus"/> so a
    /// typo surfaces as a <c>validation_error</c> rather than a silent match-nothing.
    /// </summary>
    private static LeaseStatus? ParseLeaseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Enum.TryParse<LeaseStatus>(value, ignoreCase: true, out var status) || !Enum.IsDefined(status))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "Unknown 'status'.", new { field = "status" });
        }

        return status;
    }

    /// <summary>
    /// Parses the optional <c>kind</c> query for the admin ledger-account listing (task #194): a blank
    /// value means "any kind", any other value is validated against <see cref="LedgerAccountKind"/> in its
    /// snake_case wire form (<c>user_wallet</c>, <c>platform_revenue</c>, …) so a typo is a
    /// <c>validation_error</c> rather than a silent match-nothing.
    /// </summary>
    private static LedgerAccountKind? ParseLedgerAccountKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var kind = PgEnum.ParseSnake<LedgerAccountKind>(value.Trim());
            if (Enum.IsDefined(kind))
            {
                return kind;
            }
        }
        catch (ArgumentException)
        {
            // fall through to the typed validation_error below
        }

        throw new ApiException(
            ApiErrorCode.ValidationError, "Unknown 'kind'.", new { field = "kind" });
    }

    /// <summary>
    /// Parses an optional boolean query flag ("true"/"false"/"1"/"0", case-insensitive). Blank ⇒ false;
    /// anything else ⇒ <c>validation_error</c> so a typo (<c>past_ttl=yes</c>) does not silently do nothing.
    /// </summary>
    private static bool ParseBool(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        if (value == "1")
        {
            return true;
        }

        if (value == "0")
        {
            return false;
        }

        throw new ApiException(
            ApiErrorCode.ValidationError, $"'{field}' must be a boolean.", new { field });
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    private static T Deserialize<T>(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ApiException(ApiErrorCode.ValidationError, "A request body is required.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, Json)
                ?? throw new ApiException(ApiErrorCode.ValidationError, "A request body is required.");
        }
        catch (JsonException)
        {
            throw new ApiException(ApiErrorCode.ValidationError, "The request body is not valid JSON.");
        }
    }
}
