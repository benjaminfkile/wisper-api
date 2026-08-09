namespace Wisper.Api.Domain;

/// <summary>
/// A priced allow-list entry: the overlay of <i>price</i> on one of a host's wisp images
/// (docs/DATA_MODEL.md §4, <c>host_images</c>; docs/DESIGN.md §12). wisp itself stays money-blind —
/// pricing lives only here. Pricing edits apply to <b>new</b> leases only; a running lease keeps the
/// snapshot it took at creation (§6). Unique per <c>(host_id, image_ref)</c>.
/// </summary>
public sealed record HostImage
{
    /// <summary>Row id (DB default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; init; }

    /// <summary>Owning host; deleting the host cascades to its images.</summary>
    public required Guid HostId { get; init; }

    /// <summary>Image reference — must be in the host's live wisp allow-list (<c>hello.capability</c>).</summary>
    public required string ImageRef { get; init; }

    /// <summary>Host-set price in integer cents per minute (<c>&gt;= 0</c>).</summary>
    public long PriceCentsPerMin { get; init; }

    /// <summary>The network modes the host permits for this image — a subset of its capability.</summary>
    public IReadOnlyList<NetworkMode> Networks { get; init; } = Array.Empty<NetworkMode>();

    /// <summary>Maximum lease TTL, seconds.</summary>
    public int MaxTtlSeconds { get; init; }

    /// <summary>CPU ceiling offered (<c>numeric(6,3)</c>).</summary>
    public decimal? MaxCpus { get; init; }

    /// <summary>Memory ceiling offered, MB.</summary>
    public int? MaxMemoryMb { get; init; }

    /// <summary>PID ceiling offered.</summary>
    public int? MaxPids { get; init; }

    /// <summary>
    /// The exact vCPU count this sized offer provisions per lease (task #569). <c>NULL</c> means the host's
    /// own per-lease policy default applies downstream (lease-side consumption is task #570); when present it
    /// must be positive.
    /// </summary>
    public int? Cpus { get; init; }

    /// <summary>
    /// The exact memory (MB) this sized offer provisions per lease (task #569). <c>NULL</c> means the host's
    /// own per-lease policy default applies downstream; when present it must be positive.
    /// </summary>
    public int? MemoryMb { get; init; }

    /// <summary>
    /// The exact whole exclusive GPU devices this sized offer provisions per lease (task #569). <c>0</c> means
    /// no GPU access on this offer. Validated live against the host's advertised GPU capability
    /// (<see cref="HostGpu"/>); GPU access is priced into this offer, not a separate rate table. Formerly a
    /// consumer-chosen ceiling (<c>max_gpus</c>) — now an exact count the lease provisions.
    /// </summary>
    public int Gpus { get; init; }

    /// <summary>Whether the entry is offered to consumers.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Row creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last mutation time (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
