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

            // Cap accrual at the host's last-healthy liveness point so a blind window is structurally
            // un-billable (docs/TUNNEL.md §8). A host with no live tunnel is skipped this tick — the
            // disconnect path flushes it to last-healthy and suspends it. No liveness source (unit tests)
            // means no gating: bill straight up to `now`.
            var asOf = now;
            if (_liveness is not null)
            {
                if (_liveness.LastHealthyAt(lease.HostId) is not { } lastHealthy)
                {
                    continue;
                }

                if (lastHealthy < asOf)
                {
                    asOf = lastHealthy;
                }
            }

            if (await FlushLeaseAsync(lease, asOf, ct) is not null)
            {
                flushed++;
            }
        }

        return flushed;
    }

    /// <summary>
    /// Loads the lease by id and flushes its accrued interval up to <paramref name="asOf"/> — the on-end
    /// path (flush the final, possibly sub-tick, interval before the lease transitions to <c>ended</c>).
    /// Returns <c>null</c> when there is no such lease or nothing billable to flush.
    /// </summary>
    public async Task<MeteringFlushResult?> FlushLeaseByIdAsync(
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
        return FlushLeaseAsync(lease, ComputeFinalizationWatermark(lease, now), ct);
    }

    private DateTimeOffset ComputeFinalizationWatermark(Lease lease, DateTimeOffset now)
    {
        var asOf = now;

        // TTL cap: the lease was not entitled to run past started_at + ttl_seconds, so any tail beyond
        // it is not billable regardless of when we noticed the stop (task #34).
        if (lease.StartedAt is { } startedAt)
        {
            var ttlCap = startedAt.AddSeconds(lease.TtlSeconds);
            if (ttlCap < asOf)
            {
                asOf = ttlCap;
            }
        }

        // Liveness cap: mirror RunTickAsync's rule so a blind window past last-healthy is structurally
        // un-billable (docs/TUNNEL.md §8). A null liveness source (unit tests) means no gating; a
        // liveness source that returns null (no live tunnel) leaves the cap at whatever TTL/`now` gave.
        if (_liveness?.LastHealthyAt(lease.HostId) is { } lastHealthy && lastHealthy < asOf)
        {
            asOf = lastHealthy;
        }

        return asOf;
    }

    /// <summary>
    /// Flushes the accrued metering interval for a single active lease up to <paramref name="asOf"/>:
    /// accrues the healthy seconds since the watermark, posts the <c>lease_charge</c> (fee-split from
    /// the active policy), writes the <c>lease_usage</c> row, and advances the watermark. Idempotent on
    /// <c>(lease_id, period_start)</c> — a replay after a crash between the charge and the watermark write
    /// moves no new money and preserves the cumulative-charge invariant. Returns the flush result, or
    /// <c>null</c> when nothing was billable.
    /// </summary>
    public async Task<MeteringFlushResult?> FlushLeaseAsync(
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
