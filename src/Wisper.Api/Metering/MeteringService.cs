using System.Globalization;
using Microsoft.Extensions.Logging;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Policy;

namespace Wisper.Api.Metering;

/// <summary>
/// The manager-authoritative metering engine (docs/DATA_MODEL.md §14, docs/PAYMENTS.md §4). Wisper — not
/// the host — owns billable time: for each active lease the meter accrues <see cref="Lease.BillableSeconds"/>
/// over healthy-liveness intervals only (Wisper's clock), starting at <c>lease.ready</c>
/// (<see cref="Lease.StartedAt"/>). A suspended gap never bills (docs/TUNNEL.md §8).
/// <para>
/// On each flush — the fixed tick (default 60s) and on lease end — it posts a <c>lease_charge</c> ledger
/// transaction (hold → host_earnings + platform_revenue, split by <c>platform_policy.fee_bps</c>) and
/// writes a <c>lease_usage</c> row, both idempotent on <c>(lease_id, period_start)</c>, then advances the
/// lease's <see cref="Lease.LastMeteredAt"/> watermark. A manager crash loses at most one un-flushed tick;
/// on restart the active set is reloaded and each lease resumes from its persisted watermark
/// (docs/DATA_MODEL.md §14). This is the internal ledger only — no Stripe (docs/PAYMENTS.md §2).
/// </para>
/// </summary>
public sealed class MeteringService
{
    private const int SecondsPerMinute = 60;

    private readonly ILeaseRepository _leases;
    private readonly ILeaseUsageRepository _usage;
    private readonly IHostRepository _hosts;
    private readonly LedgerService _ledger;
    private readonly PlatformPolicyService _policy;
    private readonly TimeProvider _time;
    private readonly ILogger<MeteringService> _logger;
    private readonly IMeterLivenessSource? _liveness;

    public MeteringService(
        ILeaseRepository leases,
        ILeaseUsageRepository usage,
        IHostRepository hosts,
        LedgerService ledger,
        PlatformPolicyService policy,
        TimeProvider time,
        ILogger<MeteringService> logger,
        IMeterLivenessSource? liveness = null)
    {
        _leases = leases;
        _usage = usage;
        _hosts = hosts;
        _ledger = ledger;
        _policy = policy;
        _time = time;
        _logger = logger;
        _liveness = liveness;
    }

    /// <summary>
    /// One metering tick (docs/DATA_MODEL.md §14): reloads every active lease and flushes each up to the
    /// current instant on Wisper's clock. Returns the number of leases that produced a billable flush.
    /// </summary>
    public async Task<int> RunTickAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var active = await _leases.ListActiveAsync(ct);
        var flushed = 0;
        foreach (var lease in active)
        {
            ct.ThrowIfCancellationRequested();

            // Skip a lease whose host has no live tunnel: the disconnect path flushes to last-healthy and
            // suspends. Left un-skipped, we'd charge across a blind window (docs/TUNNEL.md §8). A null
            // liveness source (unit tests) means no gating.
            if (_liveness is not null && _liveness.LastHealthyAt(lease.HostId) is null)
            {
                continue;
            }

            // The tick applies the SAME caps (TTL + liveness) as the on-end finalize path — a single source
            // of truth (task #54) means a runaway "still reported" lease past its TTL cannot accrue billable
            // seconds past started_at + ttl_seconds. A ceiling-hit lease is a cheap no-op this tick: the
            // watermark won't advance (elapsedSeconds == 0), no ledger post is attempted, and lifecycle
            // transition is left to the reconciliation paths.
            var asOf = ComputeCappedWatermark(lease, now);

            if (await FlushLeaseAsync(lease, asOf, ct) is not null)
            {
                flushed++;
            }
        }

        return flushed;
    }

    /// <summary>
    /// Loads the lease by id and flushes its accrued interval up to <paramref name="asOf"/> — the raw
    /// uncapped primitive underneath the on-end path. <b>Internal</b>: production drivers MUST route
    /// through <see cref="FinalizeLeaseAsync(Guid, DateTimeOffset, CancellationToken)"/> so the shared
    /// TTL + last-healthy cap always applies (task #60 — a single source of truth is the whole point of
    /// <see cref="ComputeCappedWatermark"/>; a raw uncapped flush from a suspend/end driver could bill
    /// past <c>started_at + ttl</c> and drain other leases' held cents through the shared
    /// <c>lease_holds</c> aggregate account). Exposed to the unit suite only.
    /// </summary>
    internal async Task<MeteringFlushResult?> FlushLeaseByIdAsync(
        Guid leaseId, DateTimeOffset asOf, CancellationToken ct = default)
    {
        var lease = await _leases.GetByIdAsync(leaseId, ct);
        return lease is null ? null : await FlushLeaseAsync(lease, asOf, ct);
    }

    /// <summary>
    /// The on-lease-end flush every finalization driver (consumer DELETE, TTL expiry / container-lost)
    /// must run BEFORE the wallet hold release, so the final billable interval — the tail between the
    /// last 60s tick and the stop — is charged and <see cref="Lease.BillableSeconds"/> reflects the full
    /// metered runtime the hold release sizes off (task #34). The flush watermark is
    /// <paramref name="now"/> capped at the lease's TTL (<c>started_at + ttl_seconds</c> — the lease was
    /// not entitled to run past it, so any post-TTL tail is not billable) and at the host's last-healthy
    /// liveness point (the same cap <see cref="RunTickAsync"/> applies — a blind window is structurally
    /// un-billable). Returns the flush result, or <c>null</c> when there is no such lease / no billable
    /// tail / the lease is no longer active (a suspended lease was already flushed to last-healthy).
    /// </summary>
    public async Task<MeteringFlushResult?> FinalizeLeaseAsync(
        Guid leaseId, DateTimeOffset now, CancellationToken ct = default)
    {
        var lease = await _leases.GetByIdAsync(leaseId, ct);
        return lease is null ? null : await FinalizeLeaseAsync(lease, now, ct);
    }

    /// <summary>
    /// Same on-end flush as <see cref="FinalizeLeaseAsync(Guid, DateTimeOffset, CancellationToken)"/>
    /// against a lease already loaded by the caller.
    /// </summary>
    public Task<MeteringFlushResult?> FinalizeLeaseAsync(
        Lease lease, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return FlushLeaseAsync(lease, ComputeCappedWatermark(lease, now), ct);
    }

    /// <summary>
    /// The single source of truth for the "as-of" watermark a flush is allowed to bill up to (task #54).
    /// Applies the TTL cap (<c>started_at + ttl_seconds</c> — the lease was not entitled to run past it,
    /// so a post-TTL tail is not billable, task #34) and the liveness cap (last-healthy — a blind window
    /// past that is structurally un-billable, docs/TUNNEL.md §8). Both the periodic tick and the on-end
    /// finalize call this so the two cannot drift: if the tick capped less strictly than finalize, the
    /// finalize's shorter watermark would land BEHIND <c>last_metered_at</c> and short-circuit to null,
    /// leaving an overcharge un-corrected.
    /// </summary>
    private DateTimeOffset ComputeCappedWatermark(Lease lease, DateTimeOffset now)
    {
        var asOf = now;

        if (lease.StartedAt is { } startedAt)
        {
            var ttlCap = startedAt.AddSeconds(lease.TtlSeconds);
            if (ttlCap < asOf)
            {
                asOf = ttlCap;
            }
        }

        // A null liveness source (unit tests) means no gating; a liveness source that returns null (no
        // live tunnel) leaves the cap at whatever TTL/`now` gave — the tick handles the "no live tunnel"
        // case by skipping the lease entirely, but the finalize path still needs to bill up to TTL.
        if (_liveness?.LastHealthyAt(lease.HostId) is { } lastHealthy && lastHealthy < asOf)
        {
            asOf = lastHealthy;
        }

        return asOf;
    }

    /// <summary>
    /// The raw uncapped flush primitive: accrues the healthy seconds since the watermark up to
    /// <paramref name="asOf"/>, posts the <c>lease_charge</c> (fee-split from the active policy), writes
    /// the <c>lease_usage</c> row, and advances the watermark. Idempotent on
    /// <c>(lease_id, period_start)</c> — a replay after a crash between the charge and the watermark write
    /// moves no new money and preserves the cumulative-charge invariant. Returns the flush result, or
    /// <c>null</c> when nothing was billable.
    /// <para>
    /// <b>Internal</b>: production callers MUST NOT invoke this directly — the whole reason
    /// <see cref="ComputeCappedWatermark"/> is the single source of truth for the "as-of" watermark
    /// (task #54, task #60) is that every tick, on-end finalize, and disconnect-suspend flush shares
    /// exactly the same TTL + last-healthy cap. Reach for
    /// <see cref="RunTickAsync"/> or <see cref="FinalizeLeaseAsync(Lease, DateTimeOffset, CancellationToken)"/>
    /// instead — they compute the cap and call through. Exposed to the unit suite so the flush
    /// primitive itself (idempotency, sub-cent deferral, zero-price accrual) can be exercised.
    /// </para>
    /// </summary>
    internal async Task<MeteringFlushResult?> FlushLeaseAsync(
        Lease lease, DateTimeOffset asOf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // Meter only over a healthy-liveness interval — an active lease. A suspended gap never bills
        // (docs/TUNNEL.md §8); a pending/ended/failed lease has no live interval to accrue.
        if (lease.Status != LeaseStatus.Active)
        {
            return null;
        }

        // The interval runs from the billed watermark (last_metered_at) — or the meter start (started_at)
        // for the very first tick — up to asOf on Wisper's clock (docs/DATA_MODEL.md §5).
        if ((lease.LastMeteredAt ?? lease.StartedAt) is not { } periodStart)
        {
            return null; // no lease.ready yet: nothing to meter
        }

        var elapsedSeconds = (long)Math.Floor((asOf - periodStart).TotalSeconds);
        if (elapsedSeconds <= 0)
        {
            return null; // the clock has not advanced past the watermark: nothing to bill
        }

        // INVARIANT (task #46, billing integrity): the rate used for every metering tick, revive re-hold,
        // end-of-lease settlement, and host payout accrual for this lease is the immutable snapshot
        // `lease.PriceCentsPerMin` stamped on the lease row at create time — NEVER the current host_images
        // price. A host that reprices an image mid-lease must not be able to change what an open lease is
        // charged. Do not read `image.PriceCentsPerMin` here (or JOIN host_images) — the snapshot is the
        // single source of truth (docs/DATA_MODEL.md §5, §6).
        var priceCentsPerMin = lease.PriceCentsPerMin;

        // Cumulative-charge accounting so per-tick integer rounding never drifts: this interval's charge
        // is (total owed at the new watermark) − (total owed so far). Over a full minute that is exactly
        // price_cents_per_min, and the running total can never exceed the up-front hold (⌈ttl/60⌉·price),
        // so the hold is never exhausted mid-run (docs/DATA_MODEL.md §8, §16).
        var newBillableTotal = lease.BillableSeconds + elapsedSeconds;
        var amountCents = ChargeCentsFor(newBillableTotal, priceCentsPerMin)
            - ChargeCentsFor(lease.BillableSeconds, priceCentsPerMin);

        // A sub-cent interval on a priced image bills nothing yet: leave the watermark where it is so the
        // seconds accumulate into the next tick until they are worth at least one cent — no value is lost.
        if (amountCents <= 0 && priceCentsPerMin > 0)
        {
            return null;
        }

        // A free image (price 0): accrue the healthy seconds against the watermark, but there is no money
        // to move and lease_usage.charge_txn_id is NOT NULL, so no ledger/usage row is written.
        if (amountCents == 0)
        {
            await AdvanceWatermarkAsync(lease, asOf, newBillableTotal, ct);
            return null;
        }

        var policy = await _policy.GetActiveOrThrowAsync(ct);
        var (platformFeeCents, hostPayoutCents) = LedgerFlows.SplitFee(amountCents, policy.FeeBps);

        // Resolve the three accounts of the charge split (docs/DATA_MODEL.md §7, §8): the singleton
        // lease_holds and platform_revenue accounts, and the host owner's host_earnings account.
        var host = await _hosts.GetByIdAsync(lease.HostId, ct)
            ?? throw new InvalidOperationException(
                $"Lease '{lease.Id}' references unknown host '{lease.HostId}'.");
        var currency = lease.Currency;
        var leaseHolds = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.LeaseHolds, null, currency, ct);
        var hostEarnings = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.HostEarnings, host.OwnerUserId, currency, ct);
        var platformRevenue = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.PlatformRevenue, null, currency, ct);

        // Post the lease_charge, keyed idempotently on (lease_id, period_start): a retried flush after a
        // crash between the charge and the watermark write returns the existing txn and moves no new money
        // (docs/DATA_MODEL.md §14). The lease_holds non-negative guard (§7d) backs the hold-never-overdrawn
        // guarantee.
        var draft = LedgerFlows.LeaseCharge(
            leaseHolds.Id, hostEarnings.Id, platformRevenue.Id, lease.Id,
            amountCents, platformFeeCents,
            idempotencyKey: ChargeIdempotencyKey(lease.Id, periodStart),
            memo: $"lease_charge {lease.Id} [{Iso(periodStart)} → {Iso(asOf)}] {elapsedSeconds}s");
        var posted = await _ledger.PostAsync(draft, ct);

        // Write the lease_usage row, idempotent on (lease_id, period_start) — a replay returns the
        // first-written row unchanged (docs/DATA_MODEL.md §6). Advance the watermark from the STORED row so
        // a de-duplicated replay preserves the cumulative-charge invariant (bill == ⌊billable·price/60⌋).
        var usage = await _usage.AppendAsync(
            new LeaseUsage
            {
                LeaseId = lease.Id,
                PeriodStart = periodStart,
                PeriodEnd = asOf,
                BillableSeconds = checked((int)elapsedSeconds),
                AmountCents = amountCents,
                PlatformFeeCents = platformFeeCents,
                HostPayoutCents = hostPayoutCents,
                ChargeTxnId = posted.Transaction.Id,
            },
            ct);

        await AdvanceWatermarkAsync(lease, usage.PeriodEnd, lease.BillableSeconds + usage.BillableSeconds, ct);

        _logger.LogInformation(
            "metered lease {LeaseId}: +{Seconds}s = {Amount}¢ (host {Host}¢ + fee {Fee}¢){Replay}",
            lease.Id, usage.BillableSeconds, usage.AmountCents, usage.HostPayoutCents, usage.PlatformFeeCents,
            posted.WasDeduplicated ? " [replay]" : string.Empty);

        return new MeteringFlushResult(usage, usage.ChargeTxnId, posted.WasDeduplicated);
    }

    /// <summary>
    /// The cents owed for <paramref name="billableSeconds"/> at <paramref name="priceCentsPerMin"/>:
    /// <c>⌊seconds·price/60⌋</c> in integer cents (money is never floats, docs/DATA_MODEL.md §1). Used as
    /// a running total so per-tick rounding never drifts.
    /// </summary>
    public static long ChargeCentsFor(long billableSeconds, long priceCentsPerMin)
    {
        if (billableSeconds <= 0 || priceCentsPerMin <= 0)
        {
            return 0;
        }

        return billableSeconds * priceCentsPerMin / SecondsPerMinute; // integer floor
    }

    /// <summary>The <c>lease_charge</c> idempotency key — stable per <c>(lease_id, period_start)</c>.</summary>
    public static string ChargeIdempotencyKey(Guid leaseId, DateTimeOffset periodStart) =>
        $"lease_charge:{leaseId:D}:{Iso(periodStart)}";

    private Task AdvanceWatermarkAsync(
        Lease lease, DateTimeOffset watermark, long billableSeconds, CancellationToken ct) =>
        _leases.TransitionStateAsync(
            lease.Id, lease.Status, lastMeteredAt: watermark, billableSeconds: billableSeconds, ct: ct);

    private static string Iso(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
