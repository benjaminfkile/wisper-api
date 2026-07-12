using System.Text.Json.Serialization;
using Wisper.Api.Domain;

namespace Wisper.Api.Leases;

/// <summary>
/// Body of <c>POST /v1/leases</c> (docs/API.md §5). All fields are nullable at the wire so the service
/// can distinguish "omitted" from a supplied value and return a precise <c>validation_error</c>. The
/// snapshots the lease keeps are resolved server-side from the referenced priced image, not trusted from
/// the client — only <c>network</c>/<c>resources</c>/<c>ttl_seconds</c>/<c>userdata</c> are request inputs.
/// </summary>
public sealed record CreateLeaseRequest(
    [property: JsonPropertyName("host_id")] string? HostId,
    [property: JsonPropertyName("host_image_id")] string? HostImageId,
    [property: JsonPropertyName("network")] string? Network,
    [property: JsonPropertyName("resources")] LeaseResourcesRequest? Resources,
    [property: JsonPropertyName("ttl_seconds")] int? TtlSeconds,
    [property: JsonPropertyName("userdata")] string? Userdata);

/// <summary>Requested resource ceilings (docs/API.md §5); each is optional and validated against the image.</summary>
public sealed record LeaseResourcesRequest(
    [property: JsonPropertyName("cpus")] double? Cpus,
    [property: JsonPropertyName("memory_mb")] int? MemoryMb,
    [property: JsonPropertyName("pids")] int? Pids);

/// <summary>
/// The <c>201</c> body of <c>POST /v1/leases</c> (docs/API.md §5): the new lease id plus the price and
/// hold snapshot the client needs to reason about cost. The <see cref="Status"/> reflects the persisted
/// lease state; the console then watches <c>GET /v1/leases/:id</c> for further transitions.
/// </summary>
public sealed record CreateLeaseResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("price_cents_per_min")] long PriceCentsPerMin,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("hold_cents")] long HoldCents,
    [property: JsonPropertyName("ttl_seconds")] int TtlSeconds,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    /// <summary>Projects a freshly created lease + its hold estimate into the 201 wire shape.</summary>
    public static CreateLeaseResponse From(LeaseCreationResult result)
    {
        var lease = result.Lease;
        return new CreateLeaseResponse(
            Id: TunnelLeaseId.Format(lease.Id),
            Status: PgEnum.ToLabel(lease.Status),
            PriceCentsPerMin: lease.PriceCentsPerMin,
            Currency: lease.Currency,
            HoldCents: result.HoldCents,
            TtlSeconds: lease.TtlSeconds,
            CreatedAt: lease.CreatedAt);
    }
}

/// <summary>The lease resource snapshot as it appears on the read surface (docs/API.md §5).</summary>
public sealed record LeaseResourcesView(
    [property: JsonPropertyName("cpus")] decimal? Cpus,
    [property: JsonPropertyName("memory_mb")] int? MemoryMb,
    [property: JsonPropertyName("pids")] int? Pids);

/// <summary>
/// The read shape of a lease (docs/API.md §5, <c>GET /v1/leases</c> and <c>GET /v1/leases/:id</c>): the
/// immutable snapshots, the current lifecycle state, and the metering timeline with a running-cost
/// figure. Until the P5 metering engine lands, <see cref="BillableSeconds"/> is the persisted watermark
/// (0 for a just-started lease) and <see cref="CostCentsSoFar"/> is derived from it — a truthful
/// placeholder, not an estimate of wall-clock time.
/// </summary>
public sealed record LeaseView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("host_id")] Guid HostId,
    [property: JsonPropertyName("image_ref")] string ImageRef,
    [property: JsonPropertyName("network")] string Network,
    [property: JsonPropertyName("resources")] LeaseResourcesView Resources,
    [property: JsonPropertyName("ttl_seconds")] int TtlSeconds,
    [property: JsonPropertyName("price_cents_per_min")] long PriceCentsPerMin,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("billable_seconds")] long BillableSeconds,
    [property: JsonPropertyName("cost_cents_so_far")] long CostCentsSoFar,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("end_reason")] string? EndReason)
{
    /// <summary>Projects a stored <see cref="Lease"/> into its read wire shape.</summary>
    public static LeaseView From(Lease lease) => new(
        Id: TunnelLeaseId.Format(lease.Id),
        Status: PgEnum.ToLabel(lease.Status),
        HostId: lease.HostId,
        ImageRef: lease.ImageRef,
        Network: PgEnum.ToLabel(lease.Network),
        Resources: new LeaseResourcesView(lease.Cpus, lease.MemoryMb, lease.Pids),
        TtlSeconds: lease.TtlSeconds,
        PriceCentsPerMin: lease.PriceCentsPerMin,
        Currency: lease.Currency,
        CreatedAt: lease.CreatedAt,
        StartedAt: lease.StartedAt,
        BillableSeconds: lease.BillableSeconds,
        CostCentsSoFar: RunningCostCents(lease),
        ExpiresAt: ExpiresAtOf(lease),
        EndReason: lease.EndReason is { } reason ? PgEnum.ToSnakeLabel(reason) : null);

    /// <summary>
    /// The running cost placeholder: billed minutes so far × price (docs/API.md §5). Metering (P5) is the
    /// authority for <c>billable_seconds</c>; here it is simply the persisted watermark, so this stays
    /// truthful (0 until the meter accrues) rather than guessing from wall-clock.
    /// </summary>
    private static long RunningCostCents(Lease lease) =>
        lease.BillableSeconds / 60 * lease.PriceCentsPerMin;

    /// <summary>When the lease's TTL elapses, measured from meter start; null until it starts.</summary>
    private static DateTimeOffset? ExpiresAtOf(Lease lease) =>
        lease.StartedAt is { } started ? started.AddSeconds(lease.TtlSeconds) : null;
}

/// <summary>A page of leases (docs/API.md §10): the <c>data</c> array plus the opaque <c>next_cursor</c>.</summary>
public sealed record LeasePage(
    [property: JsonPropertyName("data")] IReadOnlyList<LeaseView> Data,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

/// <summary>The parsed, validated <c>GET /v1/leases</c> query (status filter + cursor pagination, §10).</summary>
public sealed record LeaseListQuery
{
    /// <summary>Optional lifecycle filter; null lists every state.</summary>
    public LeaseStatus? Status { get; init; }

    /// <summary>Page size, already clamped to <c>[1, 100]</c>.</summary>
    public int Limit { get; init; }

    /// <summary>The page cursor, or null for the first page.</summary>
    public LeaseCursor? Cursor { get; init; }
}

/// <summary>
/// The result of a successful lease-create: the persisted <see cref="Lease"/> plus the
/// <see cref="HoldCents"/> the wallet gate authorized, which the 201 body echoes.
/// </summary>
public sealed record LeaseCreationResult(Lease Lease, long HoldCents);
