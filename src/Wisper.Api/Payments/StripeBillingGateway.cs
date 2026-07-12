using Stripe;

namespace Wisper.Api.Payments;

/// <summary>
/// Real <see cref="IStripeBillingGateway"/> over the Stripe SDK (docs/PAYMENTS.md §3). Each call constructs
/// the relevant Stripe service from the shared <see cref="IStripeClient.Sdk"/> and forwards the idempotency
/// key via <see cref="RequestOptions"/> where one applies. Not exercised by the unit suite (Grunt has no
/// Stripe) — the fake double backs <see cref="Wisper.Api.Billing.BillingService"/>'s tests; this type is
/// covered by the separate live-integration verification.
/// </summary>
public sealed class StripeBillingGateway : IStripeBillingGateway
{
    private readonly IStripeClient _client;

    public StripeBillingGateway(IStripeClient client) => _client = client;

    public async Task<string> CreateCustomerAsync(StripeCustomerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = new CustomerService(_client.Sdk);
        var customer = await service.CreateAsync(
            new CustomerCreateOptions
            {
                Email = request.Email,
                Metadata = new Dictionary<string, string> { [TopupMetadata.UserId] = request.UserId.ToString() },
            },
            cancellationToken: ct);
        return customer.Id;
    }

    public async Task<StripePaymentIntent> CreatePaymentIntentAsync(
        StripePaymentIntentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = new PaymentIntentService(_client.Sdk);
        var intent = await service.CreateAsync(
            new PaymentIntentCreateOptions
            {
                Amount = request.AmountCents,
                Currency = request.Currency,
                Customer = request.CustomerId,
                // The Payment Element drives SCA/3-D Secure client-side (docs/PAYMENTS.md §3).
                AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true },
                Metadata = new Dictionary<string, string>
                {
                    [TopupMetadata.UserId] = request.UserId.ToString(),
                    [TopupMetadata.Purpose] = TopupMetadata.WalletTopup,
                },
            },
            // The API idempotency key = the Stripe idempotency key, so a retried top-up returns the same
            // intent rather than creating a second charge (docs/PAYMENTS.md §1, §3).
            new RequestOptions { IdempotencyKey = request.IdempotencyKey },
            ct);
        return new StripePaymentIntent(intent.Id, intent.ClientSecret);
    }

    public async Task<StripeSetupIntent> CreateSetupIntentAsync(
        StripeSetupIntentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = new SetupIntentService(_client.Sdk);
        var intent = await service.CreateAsync(
            new SetupIntentCreateOptions
            {
                Customer = request.CustomerId,
                Usage = "off_session", // saved for future off-session top-ups / auto-recharge (§3)
                AutomaticPaymentMethods = new SetupIntentAutomaticPaymentMethodsOptions { Enabled = true },
                Metadata = new Dictionary<string, string> { [TopupMetadata.UserId] = request.UserId.ToString() },
            },
            cancellationToken: ct);
        return new StripeSetupIntent(intent.Id, intent.ClientSecret);
    }

    public async Task<StripeRefund> CreateRefundAsync(StripeRefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = new RefundService(_client.Sdk);
        var refund = await service.CreateAsync(
            new RefundCreateOptions
            {
                PaymentIntent = request.PaymentIntentId,
                Amount = request.AmountCents,
                // Stamp the user id so the charge.refunded webhook credits/debits the right wallet without a
                // lookup, mirroring the top-up path (docs/PAYMENTS.md §7, §8).
                Metadata = new Dictionary<string, string> { [TopupMetadata.UserId] = request.UserId.ToString() },
            },
            // The API idempotency key = the Stripe idempotency key, so a retried refund returns the same
            // object rather than refunding twice (docs/PAYMENTS.md §1, §7).
            new RequestOptions { IdempotencyKey = request.IdempotencyKey },
            ct);
        return new StripeRefund(refund.Id, refund.Amount);
    }
}
