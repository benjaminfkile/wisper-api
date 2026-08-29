using Wisper.Api.Domain;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Persistence.Hosts;

/// <summary>
/// Data access for <see cref="Host"/> rows (docs/DATA_MODEL.md §4). Covers owner-scoped CRUD (the
/// host API, P7.1), the online subset that feeds the consumer catalog (P4.1), agent-token-hash lookup
/// for tunnel auth, and online-state transitions driven by the tunnel lifecycle (docs/TUNNEL.md §5).
/// A Dapper + explicit-SQL implementation backs Postgres; an in-memory double backs the unit suite.
/// </summary>
public interface IHostRepository : IRepository
{
    /// <summary>Gets a host by id, or <c>null</c> if none.</summary>
    Task<Host?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>All hosts owned by <paramref name="ownerUserId"/>, newest first.</summary>
    Task<IReadOnlyList<Host>> ListByOwnerAsync(Guid ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// The admin host search (docs/API.md §8, <c>GET /v1/admin/hosts</c>): a page of hosts, newest first,
    /// optionally narrowed by <paramref name="query"/> -- a name/label substring (case-insensitive) or an
    /// exact host id. A blank query lists all. <paramref name="limit"/>/<paramref name="offset"/> paginate.
    /// </summary>
    Task<IReadOnlyList<Host>> SearchAsync(
        string? query, int limit, int offset, CancellationToken ct = default);

    /// <summary>The total number of registered hosts (docs/API.md §8 overview counts).</summary>
    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>The online hosts -- the consumer catalog set (docs/DATA_MODEL.md §4, §13).</summary>
    Task<IReadOnlyList<Host>> ListOnlineAsync(CancellationToken ct = default);

    /// <summary>Gets the host whose stored token hash equals <paramref name="agentTokenHash"/>, or <c>null</c>.</summary>
    Task<Host?> GetByAgentTokenHashAsync(string agentTokenHash, CancellationToken ct = default);

    /// <summary>Inserts a new host and returns the stored row (with any DB-generated id).</summary>
    Task<Host> CreateAsync(Host host, CancellationToken ct = default);

    /// <summary>
    /// Updates the mutable columns of the host identified by <see cref="Host.Id"/> and returns the
    /// stored row. Throws when the host does not exist.
    /// </summary>
    Task<Host> UpdateAsync(Host host, CancellationToken ct = default);

    /// <summary>
    /// Transitions a host's presence: sets <paramref name="status"/>, optionally stamps
    /// <paramref name="lastSeenAt"/> (when non-null), and bumps <c>updated_at</c>. Returns the stored
    /// row, or <c>null</c> if no such host -- the narrow write the tunnel lifecycle uses.
    /// </summary>
    Task<Host?> SetOnlineStateAsync(
        Guid id, HostStatus status, DateTimeOffset? lastSeenAt, DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the host's advertised isolation capability (<c>isolation_levels</c> + <c>default_isolation</c>,
    /// task #417) and bumps <c>updated_at</c> -- the narrow write the tunnel lifecycle uses when an agent
    /// (re)advertises. <paramref name="isolationLevels"/>/<paramref name="defaultIsolation"/> are expected
    /// already normalized (see <see cref="Wisper.Api.Domain.HostIsolation.Normalize"/>). Returns the stored
    /// row, or <c>null</c> if no such host. Presence columns are left untouched.
    /// </summary>
    Task<Host?> SetAdvertisedIsolationAsync(
        Guid id, IReadOnlyList<string> isolationLevels, string defaultIsolation, DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the host's advertised GPU capability (<c>gpu_classes</c> + <c>gpu_count</c>, task #521) and
    /// bumps <c>updated_at</c> -- the narrow write the tunnel lifecycle uses when an agent (re)advertises,
    /// mirroring <see cref="SetAdvertisedIsolationAsync"/>. <paramref name="gpuClasses"/> is expected already
    /// normalized (distinct, see <see cref="Wisper.Api.Domain.HostGpu.NormalizeClasses"/>) and
    /// <paramref name="gpuCount"/> is the total advertised devices. Returns the stored row, or <c>null</c> if
    /// no such host. Presence and isolation columns are left untouched.
    /// </summary>
    Task<Host?> SetAdvertisedGpuAsync(
        Guid id, IReadOnlyList<string> gpuClasses, int gpuCount, DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the host's <c>hello</c>-reported versions and top-level capacity (<c>wisp_version</c>,
    /// <c>agent_version</c>, <c>max_leases</c>, <c>max_streams</c>) and bumps <c>updated_at</c>. The narrow
    /// write the tunnel lifecycle uses at handshake time (task #182), so admin reads see what the connected
    /// agent advertised. A blank/whitespace <paramref name="wispVersion"/>/<paramref name="agentVersion"/> or
    /// a non-positive capacity value is stored as <c>NULL</c>. The columns are advisory surfacing only;
    /// per-host admission is enforced against the live <c>capability.capacity.max_contracts</c> snapshot
    /// (see <c>docs/TUNNEL.md</c> §5 for which value wins). Returns the stored row, or <c>null</c> if no
    /// such host. Presence / isolation / GPU columns are left untouched.
    /// </summary>
    Task<Host?> SetAdvertisedVersionsAndCapacityAsync(
        Guid id, string? wispVersion, string? agentVersion, int? maxLeases, int? maxStreams,
        DateTimeOffset updatedAt, CancellationToken ct = default);

    /// <summary>Deletes a host (cascading to its images); <c>true</c> if a row was removed.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
