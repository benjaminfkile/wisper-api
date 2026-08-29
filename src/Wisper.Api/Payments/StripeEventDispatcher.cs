using Wisper.Api.Domain;

namespace Wisper.Api.Payments;

/// <summary>
/// Routes a verified <see cref="Stripe.Event"/> to the registered <see cref="IStripeWebhookHandler"/> that
/// claims its <c>type</c> (docs/PAYMENTS.md §8.5). A recognised type is handed to its handler and reported
/// <see cref="StripeEventStatus.Processed"/>; an unrecognised type is a no-op reported
/// <see cref="StripeEventStatus.Ignored"/> (we still store it, so reconciliation can see everything Stripe
/// sent). A handler that throws propagates -- the webhook service records the event <c>failed</c> for retry.
/// The type→handler map is built once at construction; a type claimed by two handlers is a wiring bug and
/// fails fast here.
/// </summary>
public sealed class StripeEventDispatcher
{
    private readonly IReadOnlyDictionary<string, IStripeWebhookHandler> _byType;

    public StripeEventDispatcher(IEnumerable<IStripeWebhookHandler> handlers)
    {
        var map = new Dictionary<string, IStripeWebhookHandler>(StringComparer.Ordinal);
        foreach (var handler in handlers)
        {
            foreach (var type in handler.EventTypes)
            {
                if (!map.TryAdd(type, handler))
                {
                    throw new InvalidOperationException(
                        $"Two Stripe webhook handlers both claim event type '{type}': " +
                        $"{map[type].GetType().Name} and {handler.GetType().Name}.");
                }
            }
        }

        _byType = map;
    }

    /// <summary>The set of event types some handler claims (the rest are ignored no-ops).</summary>
    public IReadOnlyCollection<string> HandledTypes => (IReadOnlyCollection<string>)_byType.Keys;

    /// <summary>
    /// Dispatches <paramref name="evt"/> to its handler. Returns <see cref="StripeEventStatus.Processed"/>
    /// when a handler ran to completion, or <see cref="StripeEventStatus.Ignored"/> when no handler claims
    /// the type. Propagates any handler exception (recorded as <c>failed</c> upstream).
    /// </summary>
    public async Task<StripeEventStatus> DispatchAsync(Stripe.Event evt, CancellationToken ct = default)
    {
        if (!_byType.TryGetValue(evt.Type, out var handler))
        {
            return StripeEventStatus.Ignored;
        }

        await handler.HandleAsync(evt, ct);
        return StripeEventStatus.Processed;
    }
}
