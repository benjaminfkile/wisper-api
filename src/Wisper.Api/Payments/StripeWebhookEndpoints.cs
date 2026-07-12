using Wisper.Api.Infrastructure;

namespace Wisper.Api.Payments;

/// <summary>
/// The Stripe webhook endpoint (docs/API.md §4, docs/PAYMENTS.md §8): <c>POST /stripe/webhook</c>,
/// unauthenticated but <b>signature-verified</b> — no Cognito bearer. It reads the exact raw body (the
/// bytes the signature covers) and the <c>Stripe-Signature</c> header and hands them to
/// <see cref="StripeWebhookService"/>. A bad signature is a <c>validation_error</c> <c>400</c> with no
/// processing; a duplicate/processed/ignored event acks <c>200</c>; a handler failure answers <c>500</c>
/// so Stripe re-delivers (§8.4).
/// </summary>
public static class StripeWebhookEndpoints
{
    private const string SignatureHeader = "Stripe-Signature";

    public static void MapStripeWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Public route (like /healthz) — the signature, not a JWT, authenticates it.
        endpoints.MapPost("/stripe/webhook", IngestAsync);
    }

    private static async Task<IResult> IngestAsync(
        HttpContext http, StripeWebhookService service, CancellationToken ct)
    {
        // The raw body is authoritative — the signature is computed over these exact bytes, so we must not
        // let model binding re-serialize it. Read it verbatim as UTF-8.
        string payload;
        using (var reader = new StreamReader(http.Request.Body, System.Text.Encoding.UTF8))
        {
            payload = await reader.ReadToEndAsync(ct);
        }

        var signature = http.Request.Headers[SignatureHeader].ToString();

        StripeWebhookResult result;
        try
        {
            result = await service.IngestAsync(payload, signature, ct);
        }
        catch (StripeSignatureException ex)
        {
            // Bad/absent signature → 400, no processing (§8.1). Uniform error envelope via the middleware.
            throw new ApiException(ApiErrorCode.ValidationError, ex.Message);
        }

        var body = new { received = result.IsAck, id = result.EventId, status = result.Outcome.ToString().ToLowerInvariant() };

        // Ack 200 for dedupe/processed/ignored; 500 on handler failure so Stripe re-delivers (§8.4).
        return result.IsAck
            ? Results.Json(body)
            : Results.Json(body, statusCode: StatusCodes.Status500InternalServerError);
    }
}
