namespace Wisper.Api.Domain;

/// <summary>
/// A registered host — a machine an owner offers for leases (docs/DATA_MODEL.md §4, <c>hosts</c>).
/// The agent token is stored <b>hashed only</b>; the token itself is shown once at issuance. Presence
/// (<see cref="Status"/>) and capability/version fields are refreshed from the tunnel <c>hello</c>
/// and heartbeats (docs/TUNNEL.md §5).
/// </summary>
public sealed record Host
{
    /// <summary>Host id (DB default <c>gen_random_uuid()</c>).</summary>
    public Guid Id { get; init; }

    /// <summary>Owner — the user who registered and controls this host.</summary>
    public required Guid OwnerUserId { get; init; }

    /// <summary>Display name.</summary>
    public string? Name { get; init; }

    /// <summary>Region/label shown alongside the name.</summary>
    public string? Label { get; init; }

    /// <summary>Presence/gating state; only <see cref="HostStatus.Online"/> hosts are catalogued.</summary>
    public HostStatus Status { get; init; } = HostStatus.Offline;

    /// <summary>Argon2/bcrypt hash of the agent token — never the token itself.</summary>
    public required string AgentTokenHash { get; init; }

    /// <summary>Short non-secret prefix for identification/rotation UX.</summary>
    public string? AgentTokenPrefix { get; init; }

    /// <summary>Local wisp (<c>wispd</c>) version, from <c>hello</c>.</summary>
    public string? WispVersion { get; init; }

    /// <summary>Agent build version, from <c>hello</c>.</summary>
    public string? AgentVersion { get; init; }

    /// <summary>Advertised concurrent-lease capacity, Wisper-enforced (docs/TUNNEL.md §5).</summary>
    public int? MaxLeases { get; init; }

    /// <summary>Advertised concurrent-stream capacity, Wisper-enforced (docs/TUNNEL.md §5).</summary>
    public int? MaxStreams { get; init; }

    /// <summary>
    /// The isolation levels this host offers, from its agent capability report (docs/TUNNEL.md §5, wire
    /// field <c>isolation_levels</c>, task #417). Defaults to <c>["shared"]</c> for a host that reports
    /// nothing (an older agent); surfaced in the catalog so a consumer can filter by level.
    /// </summary>
    public IReadOnlyList<string> IsolationLevels { get; init; } = HostIsolation.SharedOnly;

    /// <summary>
    /// The isolation level this host places a lease in when none is requested (wire field
    /// <c>default_isolation</c>). Always one of <see cref="IsolationLevels"/>; <c>"shared"</c> for a host
    /// that reports nothing.
    /// </summary>
    public string DefaultIsolation { get; init; } = HostIsolation.Shared;

    /// <summary>
    /// The distinct GPU hardware classes this host offers, from its agent capability report (docs/TUNNEL.md
    /// §5, the wire <c>gpu</c> block, task #521). Opaque strings mirrored from the agent, like
    /// <see cref="IsolationLevels"/>; empty for a host that advertises no GPU (an older agent, or a machine
    /// with none). Surfaced read-only on the host views — catalog filtering lands in a later task.
    /// </summary>
    public IReadOnlyList<string> GpuClasses { get; init; } = HostGpu.NoClasses;

    /// <summary>
    /// The total number of GPU devices this host advertises (docs/TUNNEL.md §5, task #521). <c>0</c> for a
    /// host that advertises no GPU. This is the device count, not the number of distinct
    /// <see cref="GpuClasses"/>.
    /// </summary>
    public int GpuCount { get; init; }

    /// <summary>Last healthy heartbeat time (UTC).</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>Row creation time (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last mutation time (UTC).</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}
