using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.Policy;

/// <summary>
/// In-memory <see cref="IPlatformPolicyRepository"/> double for unit tests (Grunt has no Postgres).
/// Semantics mirror the SQL side: <see cref="AppendAsync"/> assigns an id when unset and
/// <see cref="GetActiveAsync"/> returns the newest version at or before the given instant. Versions with
/// distinct <c>effective_from</c> values disambiguate cleanly (as in the schema).
/// </summary>
public sealed class InMemoryPlatformPolicyRepository
    : InMemoryRepositoryBase<Guid, PlatformPolicy>, IPlatformPolicyRepository
{
    protected override Guid KeyOf(PlatformPolicy entity) => entity.Id;

    public Task<PlatformPolicy> AppendAsync(PlatformPolicy policy, CancellationToken ct = default)
    {
        var stored = policy with
        {
            Id = policy.Id == Guid.Empty ? Guid.NewGuid() : policy.Id,
            EffectiveFrom = policy.EffectiveFrom == default ? DateTimeOffset.UtcNow : policy.EffectiveFrom,
        };
        Insert(stored);
        return Task.FromResult(stored);
    }

    public Task<PlatformPolicy?> GetActiveAsync(DateTimeOffset asOf, CancellationToken ct = default) =>
        Task.FromResult(Where(p => p.EffectiveFrom <= asOf)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefault());

    public Task<IReadOnlyList<PlatformPolicy>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PlatformPolicy>>(
            All().OrderByDescending(p => p.EffectiveFrom).ToList());
}
