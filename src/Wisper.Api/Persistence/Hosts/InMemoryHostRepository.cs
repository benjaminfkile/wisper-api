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

    public Task<IReadOnlyList<Host>> ListOnlineAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Host>>(Where(h => h.Status == HostStatus.Online));

    public Task<Host?> GetByAgentTokenHashAsync(string agentTokenHash, CancellationToken ct = default) =>
        Task.FromResult(FindBy(h => h.AgentTokenHash == agentTokenHash));

    public Task<Host> CreateAsync(Host host, CancellationToken ct = default)
    {
        var stored = host.Id == Guid.Empty ? host with { Id = Guid.NewGuid() } : host;
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

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Remove(id));
}
