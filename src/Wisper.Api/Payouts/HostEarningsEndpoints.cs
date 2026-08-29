using System.Text;
using System.Text.Json;
using Wisper.Api.Accounts;
using Wisper.Api.Auth;
using Wisper.Api.Infrastructure;
using Wisper.Api.Infrastructure.Idempotency;

namespace Wisper.Api.Payouts;

/// <summary>
/// The host earnings + payout surface (docs/API.md §6, docs/PAYMENTS.md §6): <c>GET /v1/earnings</c> (accrued
/// + paid), <c>GET /v1/earnings/payouts</c> (payout history → Stripe transfer ids), and <c>POST /v1/payouts</c>
/// (on-demand payout of accrued earnings -- <c>Idempotency-Key</c> required, <c>connect_incomplete</c> → 403).
/// All are host-gated (§6) and bootstrap the caller's <c>users</c> row so earnings are scoped to a persisted
/// account. The scheduled run pays the same hosts on a cadence via <see cref="PayoutHostedService"/>.
/// </summary>
public static class HostEarningsEndpoints
{
    /// <summary>The idempotency header the money-mutating payout requires (docs/API.md §6, §9).</summary>
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private const string JsonContentType = "application/json; charset=utf-8";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapHostEarningsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/earnings", GetEarningsAsync).RequireHost();
        endpoints.MapGet("/v1/earnings/payouts", ListPayoutsAsync).RequireHost();
        endpoints.MapPost("/v1/payouts", PayoutAsync).RequireHost();
    }

    private static async Task<IResult> GetEarningsAsync(
        HttpContext http, PayoutService payouts, IUserAccountService accounts, CancellationToken ct)
    {
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await payouts.GetEarningsAsync(user.Id, ct);
        return Results.Json(result);
    }

    private static async Task<IResult> ListPayoutsAsync(
        HttpContext http, PayoutService payouts, IUserAccountService accounts, CancellationToken ct)
    {
        var user = await accounts.BootstrapAsync(http.User, ct);
        var result = await payouts.ListPayoutsAsync(user.Id, ct);
        return Results.Json(result);
    }

    private static async Task<IResult> PayoutAsync(
        HttpContext http,
        PayoutService payouts,
        IUserAccountService accounts,
        IdempotencyService idempotency,
        CancellationToken ct)
    {
        // The Idempotency-Key is REQUIRED -- a payout moves money, so a retry must never create a second
        // transfer (docs/API.md §6, §9, docs/PAYMENTS.md §6).
        var key = http.Request.Headers[IdempotencyKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                $"The {IdempotencyKeyHeader} header is required.",
                new { header = IdempotencyKeyHeader });
        }

        var body = await ReadBodyAsync(http.Request, ct);
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

        // We hold the lock. Run the payout; store the response so a later retry replays it verbatim. On failure
        // (e.g. connect_incomplete, below-minimum), release the lock so the same key can be retried once the
        // condition clears. The Stripe idempotency key = the payouts.id, so a transfer can't double-pay (§6).
        try
        {
            var payout = await payouts.PayoutOnDemandAsync(user.Id, ct);
            var view = ToView(payout);
            var json = JsonSerializer.Serialize(view, Json);
            await idempotency.CompleteAsync(key, StatusCodes.Status200OK, json, ct);
            return Results.Text(json, JsonContentType, statusCode: StatusCodes.Status200OK);
        }
        catch
        {
            await idempotency.AbandonAsync(key, CancellationToken.None);
            throw;
        }
    }

    private static PayoutView ToView(Domain.Payout payout) => new(
        payout.Id,
        payout.AmountCents,
        payout.Currency,
        Domain.PgEnum.ToSnakeLabel(payout.Status),
        payout.StripeTransferId,
        payout.Error,
        payout.PeriodStart,
        payout.PeriodEnd,
        payout.CreatedAt);

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }
}
