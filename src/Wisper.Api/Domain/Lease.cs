namespace Wisper.Api.Domain;

/// <summary>
/// A consumer's rented container instance (docs/DATA_MODEL.md §5, <c>leases</c>). At creation the lease
/// takes <b>immutable snapshots</b> of the priced image (image ref, network, the offer's sized resource
/// profile, TTL, price, currency) so a later host reprice never affects a running lease (§6). It then carries the
/// metering timeline on Wisper's clock -- <see cref="StartedAt"/> to the <see cref="LastMeteredAt"/>
/// watermark, accruing <see cref="BillableSeconds"/> only over healthy intervals (suspended gaps never
/// bill, docs/TUNNEL.md §8). The state machine is
/// <c>pending → provisioning → active ⇄ suspended → ended</c> (plus <c>provisioning → failed</c>).
/// </summary>
public sealed record Lease
{
    /// <summary>Lease id (DB default <c>gen_random_uuid()</c>); Wisper-issued and stable across reconnects.</summary>
    public Guid Id { get; init; }

    /// <summary>The consumer who rented the lease.</summary>
    public required Guid ConsumerUserId { get; init; }

    /// <summary>The host serving the container.</summary>
    public required Guid HostId { get; init; }

    /// <summary>The priced allow-list entry the lease was created from.</summary>
    public required Guid HostImageId { get; init; }

    /// <summary>Snapshot: image reference booked at creation.</summary>
    public required string ImageRef { get; init; }

    /// <summary>Snapshot: the network mode booked for this lease.</summary>
    public NetworkMode Network { get; init; }

    /// <summary>
    /// Snapshot: the resolved isolation level booked for this lease (task #418, wire field
    /// <c>isolation</c>). One of <c>shared</c> &lt; <c>sandboxed</c> &lt; <c>vm</c>, resolved server-side at
    /// create time (omitted request → <see cref="HostIsolation.Shared"/>) after the <c>min_isolation</c>
    /// policy ceiling and the target host's advertised levels are enforced; immutable thereafter.
    /// </summary>
    public string Isolation { get; init; } = HostIsolation.Shared;

    /// <summary>
    /// Snapshot: the exact vCPU count provisioned for this lease, stamped from the selected offer's sized
    /// profile at create (task #570). <c>NULL</c> = the offer left it to the host's own per-lease policy
    /// default (nothing pinned downstream); immutable thereafter. The consumer no longer chooses it -- the
    /// offer fixes it (docs/DATA_MODEL.md §4, §5).
    /// </summary>
    public int? Cpus { get; init; }

    /// <summary>
    /// Snapshot: the exact memory (MB) provisioned for this lease, stamped from the offer's sized profile at
    /// create (task #570). <c>NULL</c> = the host's own per-lease policy default applies downstream; immutable
    /// thereafter. Offer-fixed, not consumer-chosen.
    /// </summary>
    public int? MemoryMb { get; init; }

    /// <summary>
    /// Snapshot: whole exclusive GPU devices booked for this lease (task #522), stamped from the offer's sized
    /// profile at create (task #570). <c>0</c> when the offer provisions no GPU; immutable once booked.
    /// Offer-fixed, not consumer-chosen.
    /// </summary>
    public int Gpus { get; init; }

    /// <summary>Snapshot: lease TTL in seconds (<c>&gt; 0</c>).</summary>
    public int TtlSeconds { get; init; }

    /// <summary>Snapshot: price in integer cents per minute (<c>&gt;= 0</c>).</summary>
    public long PriceCentsPerMin { get; init; }

    /// <summary>Snapshot: currency (single currency <c>usd</c> for now, docs/DATA_MODEL.md §16).</summary>
    public string Currency { get; init; } = "usd";

    /// <summary>Current lifecycle state.</summary>
    public LeaseStatus Status { get; init; } = LeaseStatus.Pending;

    /// <summary>Why the lease ended; <c>null</c> until it does.</summary>
    public LeaseEndReason? EndReason { get; init; }

    /// <summary>Opaque contract id from the host's wisp (from <c>lease.ready</c>).</summary>
    public string? WispContractId { get; init; }

    /// <summary>The up-front wallet-hold ledger transaction (docs/DATA_MODEL.md §8); set by billing.</summary>
    public Guid? HoldTxnId { get; init; }

    /// <summary>Row creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Meter start -- first <c>lease.ready</c>; <c>null</c> until then.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Watermark of billed time (UTC); advances as ticks flush.</summary>
    public DateTimeOffset? LastMeteredAt { get; init; }

    /// <summary>Accrued billable seconds, excluding suspended gaps.</summary>
    public long BillableSeconds { get; init; }

    /// <summary>End time (UTC); non-null exactly when <see cref="Status"/> is <see cref="LeaseStatus.Ended"/>.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>
    /// When the lease was moved to <c>suspended</c> (task #55). Non-null while the lease is under grace
    /// management; cleared on resume (back to <c>active</c>) or revive. Persisted so the durable grace
    /// sweep can reap leases whose in-memory grace timer was lost across a restart -- the sole authoritative
    /// clock for "how long has this been suspended?" that survives an instance restart.
    /// </summary>
    public DateTimeOffset? SuspendedAt { get; init; }
}
