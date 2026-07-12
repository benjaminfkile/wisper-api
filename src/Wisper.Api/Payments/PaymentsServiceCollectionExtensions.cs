using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Payments.Handlers;

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

        // Config-driven SDK wrapper + signature verifier (real impls; fakes back the unit suite).
        services.AddSingleton<IStripeClient, StripeClient>();
        services.AddSingleton<IStripeSignatureVerifier, StripeSignatureVerifier>();

        // Webhook handler registry (docs/PAYMENTS.md §8.5) — payment/account/transfer stubs. Adding a real
        // handler later is a one-line registration; the dispatcher routes by the event types it claims.
        services.AddSingleton<IStripeWebhookHandler, PaymentWebhookHandler>();
        services.AddSingleton<IStripeWebhookHandler, AccountWebhookHandler>();
        services.AddSingleton<IStripeWebhookHandler, TransferWebhookHandler>();

        services.AddSingleton<StripeEventDispatcher>();
        services.AddSingleton<StripeWebhookService>();

        return services;
    }
}
