using Microsoft.Extensions.Options;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Ledger;
using Wisper.Api.Payments;
using Wisper.Api.Persistence.Payouts;
using Wisper.Api.Persistence.Users;

namespace Wisper.Api.Payouts;

/// <summary>
/// The host payout surface (docs/API.md §6, docs/PAYMENTS.md §6). It turns accrued <c>host_earnings</c> into
/// Stripe Connect <b>Transfers</b>: the scheduled run pays every host whose balance clears the payout minimum
/// and whose Connect account is <c>enabled</c>, and the on-demand <c>POST /v1/payouts</c> takes the same path
/// with the same guard. Every payout is one <c>payouts</c> row + one Stripe Transfer (idempotency key =
/// <c>payouts.id</c>) + one <c>payout</c> ledger txn (<c>host_earnings → platform_cash</c>), a three-way
/// tie-out (docs/PAYMENTS.md §9). A transfer that fails is recorded <c>failed</c> and posts <b>no</b> ledger
/// txn, so earnings stay in <c>host_earnings</c> and a later run retries (§6). Everything is behind interfaces
/// so the unit suite runs without Stripe/Postgres.
/// </summary>
public sealed class PayoutService
{
    /// <summary>The single currency v0 supports (docs/PAYMENTS.md §13, docs/DATA_MODEL.md §16).</summary>
    public const string Currency = "usd";

    private readonly LedgerService _ledger;
    private readonly IPayoutRepository _payouts;
    private readonly IUserRepository _users;
    private readonly IStripeConnectGateway _stripe;
    private readonly PayoutOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PayoutService> _logger;

    public PayoutService(
        LedgerService ledger,
        IPayoutRepository payouts,
        IUserRepository users,
        IStripeConnectGateway stripe,
        IOptions<PayoutOptions> options,
        TimeProvider time,
        ILogger<PayoutService> logger)
    {
        _ledger = ledger;
        _payouts = payouts;
        _users = users;
        _stripe = stripe;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>The accrued-earnings threshold a host must clear to be paid (docs/PAYMENTS.md §6).</summary>
    public long PayoutMinCents => _options.PayoutMinCents;

    /// <summary>
    /// The scheduled payout run (docs/PAYMENTS.md §6): walk every <c>host_earnings</c> account, and for each
    /// whose balance clears the minimum and whose Connect account is enabled, make a transfer. A host that
    /// throws or whose transfer fails does not stop the run (its earnings are retained and retried next run).
    /// Returns the number of hosts paid (a transfer created).
    /// </summary>
    public async Task<int> RunScheduledPayoutsAsync(CancellationToken ct = default)
    {
        var accounts = await _ledger.ListHostEarningsAccountsAsync(Currency, ct);
        var paid = 0;
        foreach (var account in accounts)
        {
            if (account.OwnerUserId is not { } hostUserId || account.BalanceCents < _options.PayoutMinCents)
            {
                continue;
            }

            try
            {
                var payout = await TryPayoutHostAsync(hostUserId, ct);
                if (payout is { Status: not PayoutStatus.Failed })
                {
                    paid++;
                }
            }
            catch (Exception ex)
            {
                // One host's failure must not stop the run — earnings are retained and retried next run (§6).
                _logger.LogError(ex, "scheduled payout for host {Host} threw; continuing", hostUserId);
            }
        }

        _logger.LogInformation("scheduled payout run paid {Count} host(s)", paid);
        return paid;
    }

    /// <summary>
    /// On-demand payout of the caller's accrued earnings (docs/API.md §6, docs/PAYMENTS.md §6) — the same path
    /// and guard as the scheduled run. Throws <see cref="ApiErrorCode.ConnectIncomplete"/> (403) when Connect
    /// is not enabled, and <see cref="ApiErrorCode.PaymentRequired"/> when the accrued balance is below the
    /// payout minimum. Otherwise returns the created payout (which may itself be <c>failed</c> if the transfer
    /// could not be made — earnings are then retained).
    /// </summary>
    public async Task<Payout> PayoutOnDemandAsync(Guid hostUserId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(hostUserId, ct)
            ?? throw new ApiException(ApiErrorCode.NotFound, "The account no longer exists.");

        if (!ConnectGate.CanReceivePayouts(user.ConnectStatus))
        {
            throw new ApiException(
                ApiErrorCode.ConnectIncomplete,
                "Stripe Connect onboarding must be complete before earnings can be paid out.",
                new { connect_status = PgEnum.ToLabel(user.ConnectStatus) });
        }

        var balance = await _ledger.GetHostEarningsCentsAsync(hostUserId, Currency, ct);
        if (balance < _options.PayoutMinCents)
        {
            throw new ApiException(
                ApiErrorCode.PaymentRequired,
                $"Accrued earnings ({balance}¢) are below the payout minimum ({_options.PayoutMinCents}¢).",
                new { accrued_cents = balance, payout_min_cents = _options.PayoutMinCents });
        }

        return await ExecutePayoutAsync(user, balance, ct);
    }

    /// <summary>
    /// The caller's earnings position (docs/API.md §6): accrued (not-yet-paid) <c>host_earnings</c>, the
    /// lifetime total transferred out, the payout threshold, and the Connect payout gate.
    /// </summary>
    public async Task<EarningsResponse> GetEarningsAsync(Guid hostUserId, CancellationToken ct = default)
    {
        var accrued = await _ledger.GetHostEarningsCentsAsync(hostUserId, Currency, ct);

        var payouts = await _payouts.ListByHostAsync(hostUserId, ct);
        long paid = 0;
        foreach (var payout in payouts)
        {
            // A committed `payout` ledger txn (payout_txn_id set) means the earnings actually left the ledger.
            if (payout.PayoutTxnId is not null)
            {
                paid += payout.AmountCents;
            }
        }

        var user = await _users.GetByIdAsync(hostUserId, ct);
        var connectStatus = user?.ConnectStatus ?? ConnectStatus.None;
        return new EarningsResponse(
            Currency,
            accrued,
            paid,
            _options.PayoutMinCents,
            PgEnum.ToLabel(connectStatus),
            ConnectGate.CanReceivePayouts(connectStatus));
    }

    /// <summary>The caller's payout history, newest-first (docs/API.md §6, <c>GET /v1/earnings/payouts</c>).</summary>
    public async Task<PayoutListResponse> ListPayoutsAsync(Guid hostUserId, CancellationToken ct = default)
    {
        var payouts = await _payouts.ListByHostAsync(hostUserId, ct);
        return new PayoutListResponse(payouts.Select(ToView).ToList());
    }

    /// <summary>Loads the host, applies the Connect + minimum guard, and pays out its current balance (or skips).</summary>
    private async Task<Payout?> TryPayoutHostAsync(Guid hostUserId, CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(hostUserId, ct);
        if (user is null)
        {
            _logger.LogWarning("host_earnings account for {Host} has no user row; skipping payout", hostUserId);
            return null;
        }

        if (!ConnectGate.CanReceivePayouts(user.ConnectStatus))
        {
            // Restricted / not-yet-enabled: hold the payout, earnings keep accruing (docs/PAYMENTS.md §5, §6).
            _logger.LogInformation(
                "payout held for host {Host}: connect_status {Status}", hostUserId, user.ConnectStatus);
            return null;
        }

        // Re-read the authoritative balance (it may have moved since enumeration) and re-check the threshold.
        var balance = await _ledger.GetHostEarningsCentsAsync(hostUserId, Currency, ct);
        if (balance < _options.PayoutMinCents)
        {
            return null;
        }

        return await ExecutePayoutAsync(user, balance, ct);
    }

    /// <summary>
    /// The shared payout core (docs/PAYMENTS.md §6, §11): create the <c>payouts</c> row, make the Stripe
    /// Transfer (idempotency key = <c>payouts.id</c>), then — only on success — post the <c>payout</c> ledger
    /// txn (<c>host_earnings → platform_cash</c>, keyed by the same id) and advance the row to <c>in_transit</c>.
    /// A transfer failure records the row <c>failed</c> with no ledger effect, so earnings are retained.
    /// </summary>
    private async Task<Payout> ExecutePayoutAsync(User user, long amountCents, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user.ConnectAccountId))
        {
            // Guarded upstream (connect_status can't be `enabled` without an account), but never transfer blind.
            throw new ApiException(
                ApiErrorCode.ConnectIncomplete, "The host has no Stripe Connect account to pay out to.");
        }

        var now = _time.GetUtcNow();
        var payout = await _payouts.CreateAsync(new Payout
        {
            HostUserId = user.Id,
            AmountCents = amountCents,
            Currency = Currency,
            Status = PayoutStatus.Pending,
            PeriodEnd = now,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);

        StripeTransfer transfer;
        try
        {
            transfer = await _stripe.CreateTransferAsync(
                new TransferRequest(
                    payout.Id, user.Id, user.ConnectAccountId, amountCents, Currency, payout.Id.ToString()),
                ct);
        }
        catch (Exception ex)
        {
            // A failed Transfer retains earnings: mark the row failed and post NO ledger txn (docs/PAYMENTS.md
            // §6). host_earnings is untouched, so the next run retries.
            var failed = await _payouts.UpdateAsync(
                payout with { Status = PayoutStatus.Failed, Error = ex.Message, UpdatedAt = _time.GetUtcNow() },
                ct);
            _logger.LogError(
                ex, "payout {Payout} transfer failed for host {Host}; earnings retained", payout.Id, user.Id);
            return failed;
        }

        // Transfer created (money is moving platform → connected balance). Post the `payout` ledger txn keyed
        // by the payouts.id, so a retried run dedupes at the ledger and can't double-drain (docs/PAYMENTS.md §6).
        var hostEarnings = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.HostEarnings, user.Id, Currency, ct);
        var platformCash = await _ledger.GetOrCreateAccountAsync(
            LedgerAccountKind.PlatformCash, null, Currency, ct);
        var posted = await _ledger.PostAsync(
            LedgerFlows.Payout(
                hostEarnings.Id, platformCash.Id, amountCents,
                idempotencyKey: payout.Id.ToString(),
                externalRef: transfer.Id,
                memo: $"payout {payout.Id} → transfer {transfer.Id} for host {user.Id}"),
            ct);

        var settled = await _payouts.UpdateAsync(
            payout with
            {
                StripeTransferId = transfer.Id,
                PayoutTxnId = posted.Transaction.Id,
                Status = PayoutStatus.InTransit,
                UpdatedAt = _time.GetUtcNow(),
            },
            ct);

        _logger.LogInformation(
            "payout {Payout} paid host {Host} {Amount}¢ via transfer {Transfer} (txn {Txn}){Replay}",
            payout.Id, user.Id, amountCents, transfer.Id, posted.Transaction.Id,
            posted.WasDeduplicated ? " [ledger replay]" : string.Empty);
        return settled;
    }

    private static PayoutView ToView(Payout payout) => new(
        payout.Id,
        payout.AmountCents,
        payout.Currency,
        PgEnum.ToSnakeLabel(payout.Status),
        payout.StripeTransferId,
        payout.Error,
        payout.PeriodStart,
        payout.PeriodEnd,
        payout.CreatedAt);
}
