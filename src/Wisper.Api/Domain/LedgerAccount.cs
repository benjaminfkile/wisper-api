namespace Wisper.Api.Domain;

/// <summary>
/// A ledger account (docs/DATA_MODEL.md §7, <c>ledger_accounts</c>) -- one of the buckets every cent in
/// Wisper lives in. Each user has exactly one <c>user_wallet</c> and (if a host) one
/// <c>host_earnings</c> account, pinned by the <c>(kind, owner_user_id)</c> unique constraint; the
/// platform singletons (<c>lease_holds</c>, <c>platform_revenue</c>, <c>platform_cash</c>,
/// <c>stripe_fees</c>) have a <c>null</c> owner. <see cref="BalanceCents"/> is a <b>maintained cache</b>
/// (a trigger updates it by the account's normal side); the journal of <see cref="LedgerEntry"/> rows is
/// the source of truth, and the reconciler re-derives the balance from them (§7e).
/// </summary>
public sealed record LedgerAccount
{
    /// <summary>Account id (DB default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; init; }

    /// <summary>Which bucket this is; fixes the account's normal side (docs/DATA_MODEL.md §7).</summary>
    public required LedgerAccountKind Kind { get; init; }

    /// <summary>Owning user for per-user accounts (<c>user_wallet</c>/<c>host_earnings</c>); <c>null</c> for platform singletons.</summary>
    public Guid? OwnerUserId { get; init; }

    /// <summary>Currency (single currency <c>usd</c> for now, docs/DATA_MODEL.md §16).</summary>
    public string Currency { get; init; } = "usd";

    /// <summary>Maintained natural (positive) balance in integer cents; derived from the journal, never authored.</summary>
    public long BalanceCents { get; init; }

    /// <summary>Row creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }
}
