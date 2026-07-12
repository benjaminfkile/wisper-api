using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Billing;
using Wisper.Api.Hosts;
using Wisper.Api.Payments.Handlers;
using Wisper.Api.Payouts;

namespace Wisper.Api.Payments;

/// <summary>
/// Wiring for the Stripe integration (docs/PAYMENTS.md §1, §8): binds <see cref="StripeOptions"/> from the
/// <c>Stripe</c> section (keys from the secrets manager, per env), registers the config-driven client
/// wrapper and signature verifier, the webhook dispatcher + ingest service, and the handler registry.
/// Handlers are stubs here (P6.2+ fill in the ledger effects). Everything is behind an interface so the
/// unit suite runs against fakes — Grunt has no Stripe.
/// </summary>
public static class PaymentsServiceCollectionExtensions
{
    public static IServiceCollection AddWisperPayments(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StripeOptions>(configuration.GetSection(StripeOptions.SectionName));

        // The shared clock (already registered by AddWisperPersistence in the running service; TryAdd keeps
        // this self-contained if payments are ever wired independently).
        services.TryAddSingleton(TimeProvider.System);

        // Config-driven SDK wrapper + signature verifier + the higher-level billing gateway (real impls;
        // fakes back the unit suite — Grunt has no Stripe).
        services.AddSingleton<IStripeClient, StripeClient>();
        services.AddSingleton<IStripeSignatureVerifier, StripeSignatureVerifier>();
        services.AddSingleton<IStripeBillingGateway, StripeBillingGateway>();
        services.AddSingleton<IStripeConnectGateway, StripeConnectGateway>();

        // Webhook handler registry (docs/PAYMENTS.md §8.5). The top-up (P6.2), account (P6.4), transfer (P6.5)
        // and refund/dispute (P6.6) handlers are all live. The dispatcher routes by event type. The payment
        // handler posts refund/chargeback + suspends on dispute; the transfer handler updates payouts.status.
        services.AddSingleton<IStripeWebhookHandler, TopupWebhookHandler>();
        services.AddSingleton<IStripeWebhookHandler, PaymentWebhookHandler>();
        services.AddSingleton<IStripeWebhookHandler, AccountWebhookHandler>();
        services.AddSingleton<IStripeWebhookHandler, TransferWebhookHandler>();

        services.AddSingleton<StripeEventDispatcher>();
        services.AddSingleton<StripeWebhookService>();

        // Consumer billing surface (docs/API.md §5, docs/PAYMENTS.md §3): top-up create + balance/usage +
        // ledger view. Depends on the ledger, lease + user repositories, active policy, and the Stripe
        // gateway — all registered above / by AddWisperPersistence.
        services.AddSingleton<BillingService>();

        // Host Connect Express onboarding surface (docs/API.md §6, docs/PAYMENTS.md §5): create/continue the
        // Connect account + Account Link, and read/derive connect_status. Depends on the connect gateway and
        // the user repository (registered above / by AddWisperPersistence).
        services.AddSingleton<HostConnectService>();

        // Host payout surface (docs/API.md §6, docs/PAYMENTS.md §6): scheduled + on-demand payouts drain accrued
        // host_earnings via Connect transfers (idempotency key = payouts.id) and post the `payout` ledger txn;
        // GET /v1/earnings + /earnings/payouts read the position. The background run cadence + minimum are config.
        services.Configure<PayoutOptions>(configuration.GetSection(PayoutOptions.SectionName));
        services.AddSingleton<PayoutService>();

        return services;
    }
}
