using Wisper.Api.Domain;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Persistence.Hosts;

/// <summary>
/// In-memory <see cref="IHostRepository"/> double for unit tests (Grunt has no Postgres). Semantics
/// mirror the SQL side: <see cref="CreateAsync"/> assigns an id when unset, list queries return the
/// documented ordering/subset, and online-state writes touch only presence columns.
/// </summary>
public sealed class InMemoryHostRepository : InMemoryRepositoryBase<Guid, Host>, IHostRepository
{
    protected override Guid KeyOf(Host entity) => entity.Id;

    public Task<Host?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Find(id));

    public Task<IReadOnlyList<Host>> ListByOwnerAsync(Guid ownerUserId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Host>>(
            Where(h => h.OwnerUserId == ownerUserId).OrderByDescending(h => h.CreatedAt).ToList());

    public Task<IReadOnlyList<Host>> SearchAsync(
        string? query, int limit, int offset, CancellationToken ct = default)
    {
        var matches = All().Where(h => MatchesQuery(h, query))
            .OrderByDescending(h => h.CreatedAt)
            .ThenByDescending(h => h.Id)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit))
            .ToList();
        return Task.FromResult<IReadOnlyList<Host>>(matches);
    }

    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(Count);

    /// <summary>Matches a host against a name/label substring (case-insensitive) or an exact id; blank matches all.</summary>
    private static bool MatchesQuery(Host host, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var trimmed = query.Trim();
        if (Guid.TryParse(trimmed, out var id) && host.Id == id)
        {
            return true;
        }

        return (host.Name?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)
            || (host.Label?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public Task<IReadOnlyList<Host>> ListOnlineAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Host>>(Where(h => h.Status == HostStatus.Online));

    public Task<Host?> GetByAgentTokenHashAsync(string agentTokenHash, CancellationToken ct = default) =>
        Task.FromResult(FindBy(h => h.AgentTokenHash == agentTokenHash));

    public Task<Host> CreateAsync(Host host, CancellationToken ct = default)
    {
        // Mirror the DB NOT NULL/default: a host that carries no advertised isolation stores ["shared"]/"shared".
        var (levels, defaultIsolation) = HostIsolation.Normalize(host.IsolationLevels, host.DefaultIsolation);
        var normalized = host with { IsolationLevels = levels, DefaultIsolation = defaultIsolation };
        var stored = normalized.Id == Guid.Empty ? normalized with { Id = Guid.NewGuid() } : normalized;
        Insert(stored);
        return Task.FromResult(stored);
    }

    public Task<Host> UpdateAsync(Host host, CancellationToken ct = default)
    {
        if (Find(host.Id) is null)
        {
            throw new InvalidOperationException($"Host '{host.Id}' does not exist.");
        }

        Upsert(host);
        return Task.FromResult(host);
    }

    public Task<Host?> SetOnlineStateAsync(
        Guid id, HostStatus status, DateTimeOffset? lastSeenAt, DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        if (Find(id) is not { } host)
        {
            return Task.FromResult<Host?>(null);
        }

        var updated = host with
        {
            Status = status,
            LastSeenAt = lastSeenAt ?? host.LastSeenAt,
            UpdatedAt = updatedAt,
        };
        Upsert(updated);
        return Task.FromResult<Host?>(updated);
    }

    public Task<Host?> SetAdvertisedIsolationAsync(
        Guid id, IReadOnlyList<string> isolationLevels, string defaultIsolation, DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        if (Find(id) is not { } host)
        {
            return Task.FromResult<Host?>(null);
        }

        var updated = host with
        {
            IsolationLevels = isolationLevels,
            DefaultIsolation = defaultIsolation,
            UpdatedAt = updatedAt,
        };
        Upsert(updated);
        return Task.FromResult<Host?>(updated);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Remove(id));
}
