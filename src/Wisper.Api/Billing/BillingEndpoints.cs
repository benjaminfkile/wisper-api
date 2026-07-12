using System.Text;
using System.Text.Json;
using Wisper.Api.Accounts;
using Wisper.Api.Auth;
using Wisper.Api.Infrastructure;
using Wisper.Api.Infrastructure.Idempotency;

namespace Wisper.Api.Billing;

/// <summary>
/// The consumer billing surface (docs/API.md §5, docs/PAYMENTS.md §3): <c>POST /v1/billing/topup</c> (create
/// a PaymentIntent, guarded by a required <c>Idempotency-Key</c>, §9), <c>GET /v1/billing</c> (balance +
/// usage summary), <c>GET /v1/billing/transactions</c> (the caller's ledger view, cursor-paginated, §10),
/// and <c>POST /v1/billing/payment-methods</c> (a SetupIntent to save a method). All are consumer-gated
/// (every authenticated user is implicitly a consumer, §2) and bootstrap the caller's <c>users</c> row so
/// billing is scoped to a persisted account. The wallet is <b>never</b> credited here — that happens only on
/// the <c>payment_intent.succeeded</c> webhook (docs/PAYMENTS.md §3, §8).
/// </summary>
public static class BillingEndpoints
{
    /// <summary>The idempotency header the money-mutating top-up requires (docs/API.md §9).</summary>
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private const string JsonContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapBillingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/billing", GetBillingAsync).RequireConsumer();
        endpoints.MapGet("/v1/billing/transactions", ListTransactionsAsync).RequireConsumer();
        endpoints.MapPost("/v1/billing/topup", TopupAsync).RequireConsumer();
        endpoints.MapPost("/v1/billing/payment-methods", CreatePaymentMethodAsync).RequireConsumer();
    }

    private static async Task<IResult> TopupAsync(
        HttpContext http,
        BillingService billing,
        IUserAccountService accounts,
        IdempotencyService idempotency,
        CancellationToken ct)
    {
        // The Idempotency-Key is REQUIRED — a top-up moves money, so a retry must never create a second
        // PaymentIntent/charge (docs/API.md §9, docs/PAYMENTS.md §3).
        var key = http.Request.Headers[IdempotencyKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"The {IdempotencyKeyHeader} header is required.",
                new { header = IdempotencyKeyHeader });
        }

        // Read the raw body once so a same-key replay (return the stored response) is told apart from a
        // same-key/different-body reuse (409 conflict), docs/API.md §9.
        var body = await ReadBodyAsync(http.Request, ct);
        var request = Deserialize(body);

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
        // failure, release the lock so the same key can be retried (the operation didn't complete). The
        // Stripe idempotency key = this API key, so a retried Stripe create returns the same intent (§3).
        try
        {
            var result = await billing.TopupAsync(user.Id, request, key, ct);
            var json = JsonSerializer.Serialize(result, Json);
            await idempotency.CompleteAsync(key, StatusCodes.Status200OK, json, ct);
            return Results.Text(json, JsonContentType, statusCode: StatusCodes.Status200OK);
        }
        catch
        {
            await idempotency.AbandonAsync(key, CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> GetBillingAsync(
        HttpContext http, BillingService billing, IUserAccountService accounts, CancellationToken ct)
    {
        var user = await accounts.BootstrapAsync(http.User, ct);
        var view = await billing.GetBillingAsync(user.Id, ct);
        return Results.Json(view);
    }

    private static async Task<IResult> ListTransactionsAsync(
        HttpContext http, BillingService billing, IUserAccountService accounts, CancellationToken ct)
    {
        var limit = ParseLimit(http.Request.Query["limit"]);
        var cursor = ParseCursor(http.Request.Query["cursor"]);
        var user = await accounts.BootstrapAsync(http.User, ct);
        var page = await billing.ListTransactionsAsync(user.Id, limit, cursor, ct);
        return Results.Json(page);
    }

    private static async Task<IResult> CreatePaymentMethodAsync(
        HttpContext http, BillingService billing, IUserAccountService accounts, CancellationToken ct)
    {
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await billing.CreatePaymentMethodSetupAsync(user.Id, ct);
        return Results.Json(result);
    }

    private static int ParseLimit(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return BillingService.DefaultLimit;
        }

        if (!int.TryParse(value, out var limit) || limit < 1 || limit > BillingService.MaxLimit)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"'limit' must be an integer between 1 and {BillingService.MaxLimit}.",
                new { field = "limit", max = BillingService.MaxLimit });
        }

        return limit;
    }

    private static BillingCursor? ParseCursor(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!BillingCursor.TryParse(value, out var cursor))
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

    private static TopupRequest Deserialize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ApiException(ApiErrorCode.ValidationError, "A request body is required.");
        }

        try
        {
            return JsonSerializer.Deserialize<TopupRequest>(body, Json)
                ?? throw new ApiException(ApiErrorCode.ValidationError, "A request body is required.");
        }
        catch (JsonException)
        {
            throw new ApiException(ApiErrorCode.ValidationError, "The request body is not valid JSON.");
        }
    }
}
