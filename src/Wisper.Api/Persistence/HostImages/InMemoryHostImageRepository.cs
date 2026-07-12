using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.HostImages;

/// <summary>
/// In-memory <see cref="IHostImageRepository"/> double for unit tests (Grunt has no Postgres).
/// Semantics mirror the SQL side: <see cref="CreateAsync"/> assigns an id when unset and rejects a
/// duplicate <c>(host_id, image_ref)</c> the way the unique constraint would, throwing
/// <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class InMemoryHostImageRepository : InMemoryRepositoryBase<Guid, HostImage>, IHostImageRepository
{
    protected override Guid KeyOf(HostImage entity) => entity.Id;

    public Task<HostImage?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Find(id));

    public Task<HostImage?> GetByHostAndRefAsync(Guid hostId, string imageRef, CancellationToken ct = default) =>
        Task.FromResult(FindBy(i => i.HostId == hostId && i.ImageRef == imageRef));

    public Task<IReadOnlyList<HostImage>> ListByHostAsync(
        Guid hostId, bool enabledOnly = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HostImage>>(
            Where(i => i.HostId == hostId && (!enabledOnly || i.Enabled))
                .OrderBy(i => i.ImageRef, StringComparer.Ordinal).ToList());

    public Task<HostImage> CreateAsync(HostImage image, CancellationToken ct = default)
    {
        var stored = image.Id == Guid.Empty ? image with { Id = Guid.NewGuid() } : image;
        GuardUnique(stored);
        Insert(stored);
        return Task.FromResult(stored);
    }

    public Task<HostImage> UpdateAsync(HostImage image, CancellationToken ct = default)
    {
        if (Find(image.Id) is null)
        {
            throw new InvalidOperationException($"HostImage '{image.Id}' does not exist.");
        }

        GuardUnique(image);
        Upsert(image);
        return Task.FromResult(image);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Remove(id));

    /// <summary>Rejects a duplicate <c>(host_id, image_ref)</c> against a different row (mirrors the DB).</summary>
    private void GuardUnique(HostImage candidate)
    {
        if (FindBy(i => i.Id != candidate.Id &&
                        i.HostId == candidate.HostId && i.ImageRef == candidate.ImageRef) is not null)
        {
            throw new InvalidOperationException(
                $"(host_id, image_ref) '({candidate.HostId}, {candidate.ImageRef})' already exists.");
        }
    }
}
