using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.HostImages;

/// <summary>
/// Data access for <see cref="HostImage"/> rows -- a host's priced allow-list (docs/DATA_MODEL.md §4).
/// CRUD is per host: the host API (P7.1) manages entries; the catalog/lease paths (P4.1-P4.3) read
/// the enabled set and resolve a specific <c>(host, image_ref)</c>. A Dapper + explicit-SQL
/// implementation backs Postgres; an in-memory double backs the unit suite. The
/// <c>(host_id, image_ref)</c> uniqueness is enforced by both.
/// </summary>
public interface IHostImageRepository : IRepository
{
    /// <summary>Gets a priced image by id, or <c>null</c> if none.</summary>
    Task<HostImage?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Resolves the priced image for a host by image reference, or <c>null</c> if none.</summary>
    Task<HostImage?> GetByHostAndRefAsync(Guid hostId, string imageRef, CancellationToken ct = default);

    /// <summary>
    /// The priced images for <paramref name="hostId"/>. When <paramref name="enabledOnly"/> is set,
    /// only entries offered to consumers are returned (the catalog view).
    /// </summary>
    Task<IReadOnlyList<HostImage>> ListByHostAsync(
        Guid hostId, bool enabledOnly = false, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new priced image and returns the stored row (with any DB-generated id). Throws when
    /// <c>(host_id, image_ref)</c> already exists.
    /// </summary>
    Task<HostImage> CreateAsync(HostImage image, CancellationToken ct = default);

    /// <summary>
    /// Updates the mutable columns of the image identified by <see cref="HostImage.Id"/> and returns
    /// the stored row. Throws when the image does not exist or the resulting <c>(host_id, image_ref)</c>
    /// would collide.
    /// </summary>
    Task<HostImage> UpdateAsync(HostImage image, CancellationToken ct = default);

    /// <summary>Deletes a priced image; <c>true</c> if a row was removed.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
