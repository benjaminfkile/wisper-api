using Wisper.Api.Payments;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A recording <see cref="IStripeWebhookHandler"/> for dispatcher/ingest tests. It claims a configurable
/// set of event types and counts invocations so a test can prove idempotent dedupe (a duplicate must not
/// re-invoke) and retry (a re-delivered failure must). Flip <see cref="Throw"/> to make it fail — the next
/// call throws, exercising the retry-safe <c>failed</c> recording path.
/// </summary>
public sealed class FakeStripeWebhookHandler : IStripeWebhookHandler
{
    public FakeStripeWebhookHandler(params string[] eventTypes) => EventTypes = eventTypes;

    public IReadOnlyCollection<string> EventTypes { get; }

    /// <summary>Number of times <see cref="HandleAsync"/> has been invoked.</summary>
    public int Calls { get; private set; }

    /// <summary>The last event handed to the handler, or <c>null</c> if never called.</summary>
    public Stripe.Event? LastEvent { get; private set; }

    /// <summary>When true, the next <see cref="HandleAsync"/> throws (simulating a handler failure).</summary>
    public bool Throw { get; set; }

    public Task HandleAsync(Stripe.Event evt, CancellationToken ct = default)
    {
        Calls++;
        LastEvent = evt;
        if (Throw)
        {
            throw new InvalidOperationException("handler boom (fake)");
        }

        return Task.CompletedTask;
    }
}
