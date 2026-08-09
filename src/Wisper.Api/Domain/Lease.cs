namespace Wisper.Api.Domain;

/// <summary>
/// A consumer's rented container instance (docs/DATA_MODEL.md §5, <c>leases</c>). At creation the lease
/// takes <b>immutable snapshots</b> of the priced image (image ref, network, resource ceilings, TTL,
/// price, currency) so a later host reprice never affects a running lease (§6). It then carries the
/// metering timeline on Wisper's clock — <see cref="StartedAt"/> to the <see cref="LastMeteredAt"/>
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

    /// <summary>Snapshot: CPU ceiling (<c>numeric(6,3)</c>), if set.</summary>
    public decimal? Cpus { get; init; }

    /// <summary>Snapshot: memory ceiling in MB, if set.</summary>
    public int? MemoryMb { get; init; }

    /// <summary>Snapshot: PID ceiling, if set.</summary>
    public int? Pids { get; init; }

    /// <summary>
    /// Snapshot: whole exclusive GPU devices booked for this lease (task #522). <c>0</c> when the offer has no
    /// GPU access or the consumer requested none; immutable once booked. Validated at create time against the
    /// offer's <see cref="HostImage.Gpus"/> count (over-ask rejects, never clamps — it is priced-in).
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

    /// <summary>Meter start — first <c>lease.ready</c>; <c>null</c> until then.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Watermark of billed time (UTC); advances as ticks flush.</summary>
    public DateTimeOffset? LastMeteredAt { get; init; }

    /// <summary>Accrued billable seconds, excluding suspended gaps.</summary>
    public long BillableSeconds { get; init; }

    /// <summary>End time (UTC); non-null exactly when <see cref="Status"/> is <see cref="LeaseStatus.Ended"/>.</summary>
    public DateTimeOffset? EndedAt { get; init; }
}
