using Wisper.Api.Payments;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A test double for <see cref="IStripeBillingGateway"/> (Grunt has no Stripe). It records every call so
/// tests can assert a customer was created once, the PaymentIntent carried the caller's user id + the API
/// idempotency key, etc., and returns canned ids/secrets without touching the network.
/// </summary>
public sealed class FakeStripeBillingGateway : IStripeBillingGateway
{
    public List<StripeCustomerRequest> CustomerCalls { get; } = new();
    public List<StripePaymentIntentRequest> PaymentIntentCalls { get; } = new();
    public List<StripeSetupIntentRequest> SetupIntentCalls { get; } = new();
    public List<StripeRefundRequest> RefundCalls { get; } = new();

    /// <summary>The customer id handed back from <see cref="CreateCustomerAsync"/>.</summary>
    public string CustomerId { get; set; } = "cus_fake";

    /// <summary>The client secret handed back from <see cref="CreatePaymentIntentAsync"/>.</summary>
    public string PaymentIntentClientSecret { get; set; } = "pi_fake_secret";

    /// <summary>The client secret handed back from <see cref="CreateSetupIntentAsync"/>.</summary>
    public string SetupIntentClientSecret { get; set; } = "seti_fake_secret";

    public Task<string> CreateCustomerAsync(StripeCustomerRequest request, CancellationToken ct = default)
    {
        CustomerCalls.Add(request);
        return Task.FromResult(CustomerId);
    }

    public Task<StripePaymentIntent> CreatePaymentIntentAsync(
        StripePaymentIntentRequest request, CancellationToken ct = default)
    {
        PaymentIntentCalls.Add(request);
        return Task.FromResult(new StripePaymentIntent($"pi_{PaymentIntentCalls.Count}", PaymentIntentClientSecret));
    }

    public Task<StripeSetupIntent> CreateSetupIntentAsync(
        StripeSetupIntentRequest request, CancellationToken ct = default)
    {
        SetupIntentCalls.Add(request);
        return Task.FromResult(new StripeSetupIntent($"seti_{SetupIntentCalls.Count}", SetupIntentClientSecret));
    }

    public Task<StripeRefund> CreateRefundAsync(StripeRefundRequest request, CancellationToken ct = default)
    {
        RefundCalls.Add(request);
        // Echo the requested amount back; a stable, per-call refund id lets tests key the ledger effect.
        return Task.FromResult(new StripeRefund($"re_{RefundCalls.Count}", request.AmountCents));
    }
}
