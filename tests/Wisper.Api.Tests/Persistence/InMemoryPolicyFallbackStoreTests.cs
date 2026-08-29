using Wisper.Api.Persistence.BillingIncidents;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="IPolicyFallbackStore"/> against the in-memory double (Grunt has no
/// Postgres; task #210, docs/PAYMENTS.md §4). Covers the four behaviours the admin overview + ack
/// endpoint rely on: appended events accumulate, the aggregate reports the newest event's fields,
/// an ack shifts the watermark and returns the pre-ack aggregate, and rows recorded before the ack
/// stop counting while rows recorded after continue to.
/// </summary>
public class InMemoryPolicyFallbackStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Empty_store_reports_zero_count_no_last_and_no_ack()
    {
        var store = new InMemoryPolicyFallbackStore();

        var aggregate = await store.GetAggregateAsync();

        Assert.Equal(0, aggregate.Count);
        Assert.Null(aggregate.LastAt);
        Assert.Null(aggregate.LastPolicyId);
        Assert.Null(aggregate.AckAt);
    }

    [Fact]
    public async Task Recording_stale_and_missing_events_accumulates_and_last_wins()
    {
        // Task #210: fallback_count sums both kinds; last_fallback_* reflects the newest occurrence
        // regardless of kind. A missing_at_flush event carries a null policy id.
        var store = new InMemoryPolicyFallbackStore();
        var stalePolicy = Guid.NewGuid();
        var lease1 = Guid.NewGuid();
        var lease2 = Guid.NewGuid();
        await store.RecordAsync(PolicyFallbackKind.StaleFallback, lease1, stalePolicy, T0);
        await store.RecordAsync(PolicyFallbackKind.MissingAtFlush, lease2, null, T0.AddMinutes(1));

        var aggregate = await store.GetAggregateAsync();

        Assert.Equal(2, aggregate.Count);
        Assert.Equal(T0.AddMinutes(1), aggregate.LastAt);
        Assert.Null(aggregate.LastPolicyId);
    }

    [Fact]
    public async Task Ack_returns_the_pre_ack_aggregate_and_clears_the_badge()
    {
        // Task #210: AckAsync returns what was cleared (so callers can audit it), and the next
        // aggregate reads zero count / null last_* until a fresh record post-ack.
        var store = new InMemoryPolicyFallbackStore();
        var policyId = Guid.NewGuid();
        await store.RecordAsync(PolicyFallbackKind.StaleFallback, Guid.NewGuid(), policyId, T0);

        var previous = await store.AckAsync(T0.AddMinutes(5));

        Assert.Equal(1, previous.Count);
        Assert.Equal(T0, previous.LastAt);
        Assert.Equal(policyId, previous.LastPolicyId);
        Assert.Null(previous.AckAt); // no prior ack existed

        var afterAck = await store.GetAggregateAsync();
        Assert.Equal(0, afterAck.Count);
        Assert.Null(afterAck.LastAt);
        Assert.Null(afterAck.LastPolicyId);
        Assert.Equal(T0.AddMinutes(5), afterAck.AckAt);
    }

    [Fact]
    public async Task Rows_recorded_after_the_ack_re_arm_the_aggregate()
    {
        // Task #210: an ack is a badge clear, not a delete. A fresh fallback after the watermark
        // must re-flip the overview to policy_fallback so a new incident is not masked.
        var store = new InMemoryPolicyFallbackStore();
        await store.RecordAsync(PolicyFallbackKind.StaleFallback, Guid.NewGuid(), Guid.NewGuid(), T0);
        var ackAt = T0.AddMinutes(5);
        await store.AckAsync(ackAt);

        // Two fresh events after the ack; the newer one owns last_at + last_policy_id.
        var newPolicyId = Guid.NewGuid();
        await store.RecordAsync(PolicyFallbackKind.StaleFallback, Guid.NewGuid(), Guid.NewGuid(), ackAt.AddMinutes(1));
        await store.RecordAsync(PolicyFallbackKind.MissingAtFlush, Guid.NewGuid(), newPolicyId, ackAt.AddMinutes(2));

        var aggregate = await store.GetAggregateAsync();

        Assert.Equal(2, aggregate.Count);
        Assert.Equal(ackAt.AddMinutes(2), aggregate.LastAt);
        Assert.Equal(newPolicyId, aggregate.LastPolicyId);
        Assert.Equal(ackAt, aggregate.AckAt);
    }

    [Fact]
    public async Task Ack_watermark_is_strict_greater_than_so_a_repeated_ack_does_not_re_expose_a_boundary_event()
    {
        // Task #210: a fallback recorded at EXACTLY the ack watermark must NOT surface after the
        // ack. The SQL predicate is occurred_at > ack_at (strict), and the in-memory double must
        // match so a boundary event stays acknowledged.
        var store = new InMemoryPolicyFallbackStore();
        await store.RecordAsync(PolicyFallbackKind.StaleFallback, Guid.NewGuid(), Guid.NewGuid(), T0);

        await store.AckAsync(T0);

        var aggregate = await store.GetAggregateAsync();
        Assert.Equal(0, aggregate.Count);
    }
}
