using Microsoft.Extensions.Logging;
using Wisper.Api.Audit;
using Wisper.Api.Billing;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Leases;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Persistence.Audit;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Policy;
using Wisper.Api.Tunnel;

namespace Wisper.Api.Admin;

/// <summary>
/// The admin API (docs/API.md §8) — the admin-group-gated operations surface: the platform overview
/// (revenue/active leases/counts derived from the ledger + repos), the versioned platform policy (read +
/// publish), host/user search and suspend/unsuspend moderation, manual refunds, the balanced ledger
/// <c>adjustment</c> (the only hand-correction of money), the audit trail, and read-only ledger forensics.
/// <b>Every write records an <c>audit_log</c> row</b> with the acting admin + before/after (docs/DATA_MODEL.md
/// §12). It sits over interfaces + the persistence repos so the unit suite runs without Postgres/Stripe.
/// </summary>
public sealed class AdminService
{
    /// <summary>The single currency v0 supports (docs/PAYMENTS.md §13, docs/DATA_MODEL.md §16).</summary>
    public const string Currency = "usd";

    /// <summary>The default admin search/list page size when the caller does not specify one (docs/API.md §10).</summary>
    public const int DefaultLimit = 25;

    /// <summary>The maximum admin search/list page size a caller may request (docs/API.md §10).</summary>
    public const int MaxLimit = 100;

    /// <summary>The maximum audit page size (docs/API.md §10).</summary>
    public const int MaxAuditLimit = 200;

    private readonly IUserRepository _users;
    private readonly IHostRepository _hosts;
    private readonly ILeaseRepository _leases;
    private readonly LedgerService _ledger;
    private readonly PlatformPolicyService _policy;
    private readonly AuditService _audit;
    private readonly BillingService _billing;
    private readonly MeteringService _meter;
    private readonly ILeaseWalletGate _walletGate;
    private readonly ITunnelRelay _relay;
    private readonly LedgerReconcileMonitor _reconcileMonitor;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminService> _logger;

    public AdminService(
        IUserRepository users,
        IHostRepository hosts,
        ILeaseRepository leases,
        LedgerService ledger,
        PlatformPolicyService policy,
        AuditService audit,
        BillingService billing,
        MeteringService meter,
        ILeaseWalletGate walletGate,
        ITunnelRelay relay,
        LedgerReconcileMonitor reconcileMonitor,
        TimeProvider time,
        ILogger<AdminService> logger)
    {
        _users = users;
        _hosts = hosts;
        _leases = leases;
        _ledger = ledger;
        _policy = policy;
        _audit = audit;
        _billing = billing;
        _meter = meter;
        _walletGate = walletGate;
        _relay = relay;
        _reconcileMonitor = reconcileMonitor;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The platform overview (docs/API.md §8): accrued platform revenue + the outstanding wallet liability +
    /// unpaid host earnings (all ledger-derived — the source of truth, docs/DATA_MODEL.md §7), the live
    /// active-lease count, and host/consumer counts.
    /// </summary>
    public async Task<AdminOverviewResponse> GetOverviewAsync(CancellationToken ct = default)
    {
        var revenue = await _ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null, Currency, ct);

        // Outstanding earmarked liabilities across all users, summed from the ledger accounts.
        var wallets = await SumBalanceByKindAsync(LedgerAccountKind.UserWallet, ct);
        var earnings = await SumBalanceByKindAsync(LedgerAccountKind.HostEarnings, ct);

        var active = await _leases.ListActiveAsync(ct);
        var hostCount = await _hosts.CountAsync(ct);
        var online = await _hosts.ListOnlineAsync(ct);
        var userCount = await _users.CountAsync(ct);

        var reconcile = _reconcileMonitor.Last is { } snap
            ? new LedgerReconcileView(
                snap.RanAt,
                snap.AccountsChecked,
                snap.DriftAccountCount,
                snap.TotalAbsoluteDriftCents,
                snap.HasDrift)
            : LedgerReconcileView.Empty;

        var health = reconcile.HasDrift ? "ledger_drift" : "ok";

        return new AdminOverviewResponse(
            Currency,
            revenue.BalanceCents,
            wallets,
            earnings,
            active.Count,
            hostCount,
            online.Count,
            userCount,
            Health: health,
            LedgerReconcile: reconcile);
    }

    /// <summary>The current policy + full version history (docs/API.md §8, <c>GET /v1/admin/policy</c>).</summary>
    public async Task<PolicyHistoryResponse> GetPolicyAsync(CancellationToken ct = default)
    {
        var active = await _policy.GetActiveAsync(ct);
        var versions = await _policy.ListVersionsAsync(ct);
        return new PolicyHistoryResponse(
            active is null ? null : PolicyView.From(active),
            versions.Select(PolicyView.From).ToList());
    }

    /// <summary>
    /// Publishes a new versioned policy (docs/API.md §8, <c>PUT /v1/admin/policy</c>, docs/DATA_MODEL.md §11):
    /// validates the fields, appends a new row (never edits one) authored by the acting admin, and records a
    /// <c>policy.update</c> audit row carrying the before/after. Returns the stored version.
    /// </summary>
    public async Task<PolicyView> PublishPolicyAsync(
        Guid adminUserId, PolicyUpdateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.FeeBps is not { } feeBps || feeBps < 0 || feeBps > 10_000)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'fee_bps' must be an integer between 0 and 10000.",
                new { field = "fee_bps" });
        }

        ValidateNonNegative(request.MinTopupCents, "min_topup_cents");
        ValidateNonNegative(request.MaxConcurrentLeasesPerUser, "max_concurrent_leases_per_user");
        ValidateNonNegative(request.MaxTtlSecondsCap, "max_ttl_seconds_cap");
        ValidateNonNegative(request.FirstTopupMaxCents, "first_topup_max_cents");
        ValidateNonNegative(request.NewAccountWindowHours, "new_account_window_hours");
        ValidateNonNegative(request.NewAccountMaxTopupCentsPerDay, "new_account_max_topup_cents_per_day");
        ValidateNonNegative(request.MaxSpendCentsPerDay, "max_spend_cents_per_day");

        // The min-isolation floor must be a requestable level (shared/sandboxed/vm) — confidential or an
        // unknown value could never be satisfied by a lease request (task #418). A blank value is normalized
        // to "no floor" so the field can be cleared.
        var minIsolation = string.IsNullOrWhiteSpace(request.MinIsolation) ? null : request.MinIsolation.Trim();
        if (minIsolation is not null && !HostIsolation.IsRequestable(minIsolation))
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'min_isolation' must be one of the requestable levels.",
                new { field = "min_isolation", allowed = HostIsolation.RequestLevels });
        }

        var previous = await _policy.GetActiveAsync(ct);

        var published = await _policy.PublishAsync(new PlatformPolicy
        {
            FeeBps = feeBps,
            MinTopupCents = request.MinTopupCents ?? 0,
            MaxConcurrentLeasesPerUser = request.MaxConcurrentLeasesPerUser,
            MaxTtlSecondsCap = request.MaxTtlSecondsCap,
            MinIsolation = minIsolation,
            FirstTopupMaxCents = request.FirstTopupMaxCents,
            NewAccountWindowHours = request.NewAccountWindowHours,
            NewAccountMaxTopupCentsPerDay = request.NewAccountMaxTopupCentsPerDay,
            MaxSpendCentsPerDay = request.MaxSpendCentsPerDay,
            EffectiveFrom = request.EffectiveFrom ?? default,
            CreatedBy = adminUserId,
        }, ct);

        await _audit.RecordAsync(
            "policy.update",
            actorUserId: adminUserId,
            targetType: "platform_policy",
            targetId: published.Id,
            meta: new
            {
                before = previous is null ? null : PolicyView.From(previous),
                after = PolicyView.From(published),
            },
            ct);

        return PolicyView.From(published);
    }

    /// <summary>A page of the user search (docs/API.md §8, <c>GET /v1/admin/users</c>).</summary>
    public async Task<AdminPage<UserAdminView>> SearchUsersAsync(
        string? query, int limit, int offset, CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(limit, 1, MaxLimit);
        // Over-fetch one to learn whether another page exists without a second count query.
        var rows = await _users.SearchAsync(query, pageSize + 1, offset, ct);
        var (page, next) = Paginate(rows, pageSize, offset);
        return new AdminPage<UserAdminView>(page.Select(UserAdminView.From).ToList(), next);
    }

    /// <summary>A page of the host search (docs/API.md §8, <c>GET /v1/admin/hosts</c>).</summary>
    public async Task<AdminPage<HostAdminView>> SearchHostsAsync(
        string? query, int limit, int offset, CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(limit, 1, MaxLimit);
        var rows = await _hosts.SearchAsync(query, pageSize + 1, offset, ct);
        var (page, next) = Paginate(rows, pageSize, offset);
        return new AdminPage<HostAdminView>(page.Select(HostAdminView.From).ToList(), next);
    }

    /// <summary>
    /// Suspends or reinstates a user (docs/API.md §8, moderation): sets <c>user_status</c>
    /// (<c>suspended</c> ⇄ <c>active</c>) and records a <c>user.suspend</c>/<c>user.unsuspend</c> audit row
    /// with the before/after. A no-op transition (already in the target state) is idempotent and still audited.
    /// Throws <c>not_found</c> when no such user, and <c>conflict</c> when the account is deleted.
    /// </summary>
    public async Task<UserAdminView> SetUserSuspendedAsync(
        Guid adminUserId, Guid userId, bool suspend, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, $"No such user '{userId}'.");

        if (user.Status == UserStatus.Deleted)
        {
            throw new ApiException(ApiErrorCode.Conflict, "A deleted account cannot be moderated.");
        }

        var before = user.Status;
        var after = suspend ? UserStatus.Suspended : UserStatus.Active;
        var updated = before == after
            ? user
            : await _users.UpdateAsync(user with { Status = after, UpdatedAt = _time.GetUtcNow() }, ct);

        await _audit.RecordAsync(
            suspend ? "user.suspend" : "user.unsuspend",
            actorUserId: adminUserId,
            targetType: "user",
            targetId: userId,
            meta: new { before = PgEnum.ToLabel(before), after = PgEnum.ToLabel(after) },
            ct);

        return UserAdminView.From(updated);
    }

    /// <summary>
    /// Suspends or reinstates a host (docs/API.md §8, moderation): sets <c>host_status</c> to
    /// <c>suspended</c> (removing it from the consumer catalog) or, on reinstate, back to <c>offline</c> (the
    /// tunnel lifecycle brings it back <c>online</c> on the next healthy connect). Records a
    /// <c>host.suspend</c>/<c>host.unsuspend</c> audit row with the before/after. Throws <c>not_found</c> when
    /// no such host.
    /// </summary>
    public async Task<HostAdminView> SetHostSuspendedAsync(
        Guid adminUserId, Guid hostId, bool suspend, CancellationToken ct = default)
    {
        var host = await _hosts.GetByIdAsync(hostId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, $"No such host '{hostId}'.");

        var before = host.Status;
        // Reinstating a host returns it to offline; the tunnel lifecycle re-advertises online on reconnect.
        var after = suspend ? HostStatus.Suspended : HostStatus.Offline;
        var updated = before == after
            ? host
            : await _hosts.UpdateAsync(host with { Status = after, UpdatedAt = _time.GetUtcNow() }, ct);

        await _audit.RecordAsync(
            suspend ? "host.suspend" : "host.unsuspend",
            actorUserId: adminUserId,
            targetType: "host",
            targetId: hostId,
            meta: new { before = PgEnum.ToLabel(before), after = PgEnum.ToLabel(after) },
            ct);

        return HostAdminView.From(updated);
    }

    /// <summary>
    /// The admin manual refund (docs/API.md §8, <c>POST /v1/admin/refunds</c>): validates the target user, then
    /// delegates to <see cref="BillingService.AdminRefundAsync"/> — the same Stripe refund + <c>refund</c>
    /// ledger txn the self-serve path posts, recorded with the acting admin as the audit actor.
    /// </summary>
    public async Task<RefundResponse> RefundAsync(
        Guid adminUserId, AdminRefundRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.UserId is not { } targetUserId || targetUserId == Guid.Empty)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "'user_id' is required.", new { field = "user_id" });
        }

        return await _billing.AdminRefundAsync(
            adminUserId,
            targetUserId,
            new RefundRequest(request.AmountCents, request.PaymentIntent),
            idempotencyKey,
            ct);
    }

    /// <summary>
    /// The balanced ledger <c>adjustment</c> — the <b>only</b> way to hand-correct money (docs/API.md §8,
    /// docs/DATA_MODEL.md §7, §12): posts a two-legged transaction moving <c>amount_cents</c> from the debit
    /// account to the credit account (keyed by <paramref name="idempotencyKey"/> so a retry can't double-post),
    /// then records a <c>ledger.adjustment</c> audit row with the amount + reason. Throws <c>validation_error</c>
    /// on bad input, <c>not_found</c> for an unknown account, and <c>insufficient_funds</c>/<c>conflict</c> when
    /// the move would over-draw an earmarked liability (§7d).
    /// </summary>
    public async Task<AdjustmentResponse> AdjustAsync(
        Guid adminUserId, AdjustmentRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        if (request.AmountCents is not { } amountCents || amountCents <= 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "'amount_cents' must be a positive integer.",
                new { field = "amount_cents" });
        }

        if (request.DebitAccountId is not { } debitId || debitId == Guid.Empty)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "'debit_account_id' is required.", new { field = "debit_account_id" });
        }

        if (request.CreditAccountId is not { } creditId || creditId == Guid.Empty)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "'credit_account_id' is required.", new { field = "credit_account_id" });
        }

        if (debitId == creditId)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError,
                "An adjustment must move between two distinct accounts.",
                new { field = "credit_account_id" });
        }

        // Both accounts must exist — never post against a phantom account (§7). 404 leaks nothing money-side.
        var debit = await _ledger.GetAccountAsync(debitId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, $"No such ledger account '{debitId}'.");
        var credit = await _ledger.GetAccountAsync(creditId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, $"No such ledger account '{creditId}'.");

        PostedTransaction posted;
        try
        {
            posted = await _ledger.PostAsync(
                LedgerFlows.Adjustment(
                    debit.Id, credit.Id, amountCents,
                    idempotencyKey: AdjustmentIdempotencyKey(idempotencyKey),
                    memo: request.Reason is { } reason && !string.IsNullOrWhiteSpace(reason)
                        ? $"adjustment: {reason}"
                        : $"adjustment {amountCents}¢ {debit.Id} → {credit.Id}"),
                ct);
        }
        catch (LedgerException ex) when (ex.Reason == LedgerViolation.InsufficientFunds)
        {
            throw new ApiException(
                ApiErrorCode.InsufficientFunds,
                "The adjustment would over-draw the debited wallet.",
                new { account_id = debit.Id, amount_cents = amountCents });
        }
        catch (LedgerException ex) when (ex.Reason == LedgerViolation.HoldOverdrawn)
        {
            throw new ApiException(
                ApiErrorCode.Conflict,
                "The adjustment would over-draw the debited hold account.",
                new { account_id = debit.Id, amount_cents = amountCents });
        }

        var debitBalance = await _ledger.GetBalanceAsync(debit.Id, ct);
        var creditBalance = await _ledger.GetBalanceAsync(credit.Id, ct);

        await _audit.RecordAsync(
            "ledger.adjustment",
            actorUserId: adminUserId,
            targetType: "ledger_transaction",
            targetId: posted.Transaction.Id,
            meta: new
            {
                debit_account_id = debit.Id,
                credit_account_id = credit.Id,
                amount_cents = amountCents,
                reason = request.Reason,
                replayed = posted.WasDeduplicated,
            },
            ct);

        return new AdjustmentResponse(
            posted.Transaction.Id, amountCents, debit.Id, credit.Id, debitBalance, creditBalance);
    }

    /// <summary>
    /// The admin lease listing (docs/API.md §8, task #57) — a page of non-terminal leases
    /// (<c>active + suspended</c>), the set an operator needs to find stuck leases without SQL. Optional
    /// <paramref name="status"/> narrows to one lifecycle state; <paramref name="pastTtl"/> keeps only
    /// leases whose <c>started_at + ttl_seconds</c> has already elapsed. Ordered oldest-first (the strays
    /// come first) and paged with the same offset shape as the host/user search.
    /// </summary>
    public async Task<AdminPage<AdminLeaseView>> ListLeasesAsync(
        LeaseStatus? status, bool pastTtl, int limit, int offset, CancellationToken ct = default)
    {
        var pageSize = Math.Clamp(limit, 1, MaxLimit);
        if (offset < 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, "'offset' must be a non-negative integer.", new { field = "offset" });
        }

        var all = await _leases.ListNonTerminalAsync(ct);
        var now = _time.GetUtcNow();
        var filtered = all
            .Where(l => status is not { } s || l.Status == s)
            .Where(l => !pastTtl || IsPastTtl(l, now))
            .Skip(offset)
            .Take(pageSize + 1)
            .ToList();
        var (page, next) = Paginate(filtered, pageSize, offset);
        return new AdminPage<AdminLeaseView>(page.Select(AdminLeaseView.From).ToList(), next);
    }

    /// <summary>
    /// The operator escape hatch (docs/API.md §8, task #57): force-end a stuck lease from ANY non-terminal
    /// state — the same three-step end path the tunnel reconciliation drivers use (finalize billing → CAS
    /// transition to <c>ended (admin)</c> → release the wallet hold), plus a best-effort
    /// <c>lease.release</c> down the tunnel so a live host tears the container down (wisp's TTL reaper
    /// covers a host with no live tunnel — no need to wait). Idempotent: an already-terminal lease is a
    /// no-op success (no double hold release), an unknown lease is <c>404</c>. Records a
    /// <c>lease.admin_end</c> audit row with the before/after status.
    /// </summary>
    public async Task<AdminLeaseView> ForceEndLeaseAsync(
        Guid adminUserId, Guid leaseId, CancellationToken ct = default)
    {
        var lease = await _leases.GetByIdAsync(leaseId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, $"No such lease '{leaseId}'.");

        // Idempotent: an already-terminal (ended/failed) lease is a safe no-op replay — the wallet hold
        // was released the first time around and re-issuing the release keyed to the same hold generation
        // would move nothing anyway; still, skipping the whole path avoids re-notifying the host and a
        // duplicate audit row for something that already happened.
        if (lease.Status is LeaseStatus.Ended or LeaseStatus.Failed)
        {
            return AdminLeaseView.From(lease);
        }

        var now = _time.GetUtcNow();
        var before = lease.Status;

        // Only an active lease has a billable tail to flush — a suspended lease was already flushed to
        // last-healthy at suspend time, and pending/provisioning never accrued (docs/TUNNEL.md §8).
        // FinalizeLeaseAsync applies the same TTL + last-healthy caps the periodic tick and every other
        // finalize driver use (task #54), so the consumer is charged exactly the healthy seconds up to now
        // and never past what the lease was entitled to run.
        if (lease.Status == LeaseStatus.Active)
        {
            await _meter.FinalizeLeaseAsync(lease, now, ct);
        }

        // CAS-guarded transition on the pre-flush status: if the sweep / heartbeat set-diff / a consumer
        // release won the race the WHERE clause fails, TransitionStateAsync returns null, and we do NOT
        // re-release the hold below — mirroring the discipline used elsewhere for the end path.
        var moved = await _leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Admin, endedAt: now,
            expectedCurrentStatus: lease.Status, ct: ct);
        if (moved is null)
        {
            var latest = await _leases.GetByIdAsync(leaseId, ct);
            return AdminLeaseView.From(latest ?? lease);
        }

        // Return the unused hold to the wallet (docs/PAYMENTS.md §4). Keyed per hold generation so
        // repeated end drivers converge on a single release — the CAS above already gives zero-post
        // idempotence on the state transition itself.
        await _walletGate.ReleaseHoldAsync(lease.Id, ct);

        // Best-effort tunnel notify (task #57): if the host has a live tunnel, tear the container down
        // via lease.release. No live tunnel (host_offline) or a slow host (upstream_timeout) is not a
        // failure — wisp's local TTL reaper reclaims the container regardless (docs/TUNNEL.md §8), and
        // an admin already committed the ledger-side end above.
        await TryReleaseOverTunnelAsync(lease, ct);

        await _audit.RecordAsync(
            "lease.admin_end",
            actorUserId: adminUserId,
            targetType: "lease",
            targetId: leaseId,
            meta: new
            {
                before = PgEnum.ToLabel(before),
                after = PgEnum.ToLabel(LeaseStatus.Ended),
                end_reason = PgEnum.ToSnakeLabel(LeaseEndReason.Admin),
            },
            ct);

        _logger.LogWarning(
            "lease {LeaseId} force-ended (admin) by {AdminUserId} from {Before}; hold released",
            lease.Id, adminUserId, PgEnum.ToLabel(before));

        return AdminLeaseView.From(moved);
    }

    /// <summary>
    /// Fires-and-forgets <c>lease.release</c> down the host's tunnel (task #57). A missing tunnel
    /// (<c>host_offline</c>), a silent host (<c>upstream_timeout</c>), or any other typed relay failure is
    /// swallowed here — wisp's local TTL reaper reclaims the container regardless, and an admin has
    /// already committed the ledger-side end. The exception must not shadow the completed end.
    /// </summary>
    private async Task TryReleaseOverTunnelAsync(Lease lease, CancellationToken ct)
    {
        try
        {
            await _relay.ReleaseAsync(lease.HostId.ToString(), TunnelLeaseId.Format(lease.Id), ct);
        }
        catch (ApiException ex)
        {
            _logger.LogInformation(
                "admin end: tunnel notify skipped for lease {LeaseId} on host {HostId} ({Code}: {Message}); " +
                "wisp TTL reaper will reclaim the container",
                lease.Id, lease.HostId, ex.Code, ex.Message);
        }
    }

    /// <summary>The audit trail page (docs/API.md §8, <c>GET /v1/admin/audit</c>).</summary>
    public async Task<IReadOnlyList<AuditLogEntry>> QueryAuditAsync(
        AuditLogQuery query, CancellationToken ct = default) =>
        await _audit.QueryAsync(query, ct);

    /// <summary>
    /// Read-only ledger forensics (docs/API.md §8, <c>GET /v1/admin/ledger/accounts/:id</c>): the account plus
    /// its journal entries, newest first. Throws <c>not_found</c> when no such account.
    /// </summary>
    public async Task<LedgerAccountForensicsResponse> GetLedgerAccountAsync(
        Guid accountId, CancellationToken ct = default)
    {
        var account = await _ledger.GetAccountAsync(accountId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, $"No such ledger account '{accountId}'.");

        var entries = await _ledger.ListEntriesForAccountAsync(accountId, ct);
        var view = entries
            .OrderByDescending(e => e.Id)
            .Select(LedgerEntryView.From)
            .ToList();
        return new LedgerAccountForensicsResponse(LedgerAccountView.From(account), view);
    }

    /// <summary>The <c>adjustment</c> ledger idempotency key — namespaced under the admin's API idempotency key.</summary>
    public static string AdjustmentIdempotencyKey(string apiKey) => $"adjustment:{apiKey}";

    /// <summary>A lease's TTL has elapsed on Wisper's clock (task #57) — used by the admin listing's
    /// <c>past_ttl</c> filter. A lease with no <see cref="Lease.StartedAt"/> (never went ready) is not
    /// past its TTL yet — the clock only runs from meter start.</summary>
    private static bool IsPastTtl(Lease lease, DateTimeOffset now) =>
        lease.StartedAt is { } started && started.AddSeconds(lease.TtlSeconds) < now;

    /// <summary>Sums the maintained balance across every ledger account of <paramref name="kind"/>.</summary>
    private async Task<long> SumBalanceByKindAsync(LedgerAccountKind kind, CancellationToken ct)
    {
        var accounts = await _ledger.ListAccountsByKindAsync(kind, Currency, ct);
        return accounts.Sum(a => a.BalanceCents);
    }

    /// <summary>
    /// Splits an over-fetched (<c>pageSize + 1</c>) result into the page + the next offset (or <c>null</c>
    /// when the page is the last).
    /// </summary>
    private static (IReadOnlyList<T> Page, int? NextOffset) Paginate<T>(
        IReadOnlyList<T> overFetched, int pageSize, int offset)
    {
        if (overFetched.Count <= pageSize)
        {
            return (overFetched, null);
        }

        return (overFetched.Take(pageSize).ToList(), offset + pageSize);
    }

    private static void ValidateNonNegative(long? value, string field)
    {
        if (value is < 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, $"'{field}' must be non-negative.", new { field });
        }
    }

    private static void ValidateNonNegative(int? value, string field)
    {
        if (value is < 0)
        {
            throw new ApiException(
                ApiErrorCode.ValidationError, $"'{field}' must be non-negative.", new { field });
        }
    }
}
