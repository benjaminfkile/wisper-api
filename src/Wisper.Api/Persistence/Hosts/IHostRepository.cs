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

    /// <summary>The online hosts — the consumer catalog set (docs/DATA_MODEL.md §4, §13).</summary>
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
    /// row, or <c>null</c> if no such host — the narrow write the tunnel lifecycle uses.
    /// </summary>
    Task<Host?> SetOnlineStateAsync(
        Guid id, HostStatus status, DateTimeOffset? lastSeenAt, DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>Deletes a host (cascading to its images); <c>true</c> if a row was removed.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
