namespace Wisper.Api.Payments;

/// <summary>
/// Stripe configuration (docs/PAYMENTS.md §1, §10), bound from the <see cref="SectionName"/> section. Keys
/// live in a secrets manager per environment and are supplied here as config — <b>never</b> in code. The
/// <c>wisper_dev</c> environment carries Stripe <b>test</b> keys, <c>wisper_prod</c> carries <b>live</b>
/// keys, each with its own webhook endpoint and signing secret. When unset the wrapper and the webhook
/// verifier <b>fail closed</b>, so a Grunt boot with no Stripe configured cannot silently accept traffic.
/// </summary>
public sealed class StripeOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Stripe";

    /// <summary>
    /// The Stripe secret API key (<c>sk_test_…</c> / <c>sk_live_…</c>) used for every outbound Stripe
    /// write. Least-privilege, per env, from the secrets manager. Null/blank ⇒ the client is unconfigured.
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// The webhook endpoint's signing secret (<c>whsec_…</c>) used to verify the <c>Stripe-Signature</c>
    /// header (docs/PAYMENTS.md §8). Separate from <see cref="SecretKey"/> and per endpoint/env. Null/blank
    /// ⇒ signature verification fails closed (no webhook is processed).
    /// </summary>
    public string? WebhookSigningSecret { get; set; }

    /// <summary>
    /// The signature-freshness tolerance in seconds — the maximum accepted age of the timestamp inside
    /// <c>Stripe-Signature</c> (Stripe's default is 300s). Guards against replay of an old signed body.
    /// </summary>
    public long WebhookToleranceSeconds { get; set; } = 300;

    /// <summary>True when an API secret key is present (outbound Stripe calls are possible).</summary>
    public bool HasSecretKey => !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>True when a webhook signing secret is present (inbound webhooks can be verified).</summary>
    public bool HasWebhookSecret => !string.IsNullOrWhiteSpace(WebhookSigningSecret);
}
