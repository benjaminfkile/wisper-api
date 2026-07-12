using Dapper;
using Wisper.Api.Domain;

namespace Wisper.Api.Persistence.HostImages;

/// <summary>
/// Dapper + explicit-SQL <see cref="IHostImageRepository"/> over Postgres (docs/DATA_MODEL.md §4).
/// The <c>network_mode[]</c> column has no direct Dapper mapping, so it is read via a <c>::text[]</c>
/// cast into a <see cref="Row"/> of string labels and projected to <see cref="NetworkMode"/>, and
/// written as a <c>text[]</c> parameter cast to <c>network_mode[]</c>. Not exercised by the unit suite
/// (Grunt has no Postgres); the in-memory double is.
/// </summary>
public sealed class HostImageRepository : RepositoryBase, IHostImageRepository
{
    // networks cast to text[] so it maps to Row.Networks; scalar snake_case columns map by underscore.
    private const string Columns =
        "id, host_id, image_ref, price_cents_per_min, networks::text[] AS networks, max_ttl_seconds, " +
        "max_cpus, max_memory_mb, max_pids, enabled, created_at, updated_at";

    public HostImageRepository(Db db) : base(db)
    {
    }

    public async Task<HostImage?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM host_images WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task<HostImage?> GetByHostAndRefAsync(Guid hostId, string imageRef, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM host_images WHERE host_id = @hostId AND image_ref = @imageRef",
            new { hostId, imageRef }, cancellationToken: ct));
        return row?.ToEntity();
    }

    public async Task<IReadOnlyList<HostImage>> ListByHostAsync(
        Guid hostId, bool enabledOnly = false, CancellationToken ct = default)
    {
        var sql = $"SELECT {Columns} FROM host_images WHERE host_id = @hostId" +
                  (enabledOnly ? " AND enabled = true" : string.Empty) +
                  " ORDER BY image_ref";

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, new { hostId }, cancellationToken: ct));
        return rows.Select(r => r.ToEntity()).ToList();
    }

    public async Task<HostImage> CreateAsync(HostImage image, CancellationToken ct = default)
    {
        const string sql = $"""
            INSERT INTO host_images (id, host_id, image_ref, price_cents_per_min, networks, max_ttl_seconds,
                                     max_cpus, max_memory_mb, max_pids, enabled, created_at, updated_at)
            VALUES (COALESCE(@Id, gen_random_uuid()), @HostId, @ImageRef, @PriceCentsPerMin,
                    @Networks::network_mode[], @MaxTtlSeconds, @MaxCpus, @MaxMemoryMb, @MaxPids, @Enabled,
                    COALESCE(@CreatedAt, now()), COALESCE(@UpdatedAt, now()))
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleAsync<Row>(new CommandDefinition(sql, ToParameters(image), cancellationToken: ct));
        return row.ToEntity();
    }

    public async Task<HostImage> UpdateAsync(HostImage image, CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE host_images
               SET image_ref           = @ImageRef,
                   price_cents_per_min  = @PriceCentsPerMin,
                   networks             = @Networks::network_mode[],
                   max_ttl_seconds      = @MaxTtlSeconds,
                   max_cpus             = @MaxCpus,
                   max_memory_mb        = @MaxMemoryMb,
                   max_pids             = @MaxPids,
                   enabled              = @Enabled,
                   updated_at           = COALESCE(@UpdatedAt, now())
             WHERE id = @Id
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(sql, ToParameters(image), cancellationToken: ct));
        return row?.ToEntity() ?? throw new InvalidOperationException($"HostImage '{image.Id}' does not exist.");
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM host_images WHERE id = @id", new { id }, cancellationToken: ct));
        return affected > 0;
    }

    /// <summary>Projects a <see cref="HostImage"/> to SQL parameters; networks as their text labels.</summary>
    private static object ToParameters(HostImage image) => new
    {
        Id = image.Id == Guid.Empty ? (Guid?)null : image.Id,
        image.HostId,
        image.ImageRef,
        image.PriceCentsPerMin,
        Networks = image.Networks.Select(PgEnum.ToLabel).ToArray(),
        image.MaxTtlSeconds,
        image.MaxCpus,
        image.MaxMemoryMb,
        image.MaxPids,
        image.Enabled,
        CreatedAt = image.CreatedAt == default ? (DateTimeOffset?)null : image.CreatedAt,
        UpdatedAt = image.UpdatedAt == default ? (DateTimeOffset?)null : image.UpdatedAt,
    };

    /// <summary>
    /// Dapper projection of a <c>host_images</c> row: the <c>network_mode[]</c> column arrives as
    /// string labels (read via <c>::text[]</c>) and is parsed to <see cref="NetworkMode"/> in
    /// <see cref="ToEntity"/>. Property names map to the aliased columns via
    /// <c>MatchNamesWithUnderscores</c>.
    /// </summary>
    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid HostId { get; init; }
        public string ImageRef { get; init; } = string.Empty;
        public long PriceCentsPerMin { get; init; }
        public string[] Networks { get; init; } = Array.Empty<string>();
        public int MaxTtlSeconds { get; init; }
        public decimal? MaxCpus { get; init; }
        public int? MaxMemoryMb { get; init; }
        public int? MaxPids { get; init; }
        public bool Enabled { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public HostImage ToEntity() => new()
        {
            Id = Id,
            HostId = HostId,
            ImageRef = ImageRef,
            PriceCentsPerMin = PriceCentsPerMin,
            Networks = Networks.Select(PgEnum.Parse<NetworkMode>).ToList(),
            MaxTtlSeconds = MaxTtlSeconds,
            MaxCpus = MaxCpus,
            MaxMemoryMb = MaxMemoryMb,
            MaxPids = MaxPids,
            Enabled = Enabled,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }
}
