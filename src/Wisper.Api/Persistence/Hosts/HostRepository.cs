using Dapper;
using Wisper.Api.Domain;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Persistence.Hosts;

/// <summary>
/// Dapper + explicit-SQL <see cref="IHostRepository"/> over Postgres (docs/DATA_MODEL.md §4). The
/// <c>status</c> enum is written as a text parameter cast to <c>host_status</c> and read back with a
/// <c>::text</c> cast; snake_case columns map to PascalCase via <c>MatchNamesWithUnderscores</c>
/// (set at wiring). Not exercised by the unit suite (Grunt has no Postgres); the in-memory double is.
/// </summary>
public sealed class HostRepository : RepositoryBase, IHostRepository
{
    private const string Columns =
        "id, owner_user_id, name, label, status::text AS status, agent_token_hash, agent_token_prefix, " +
        "wisp_version, agent_version, max_leases, max_streams, isolation_levels::text[] AS isolation_levels, " +
        "default_isolation, gpu_classes::text[] AS gpu_classes, gpu_count, last_seen_at, created_at, updated_at";

    public HostRepository(Db db) : base(db)
    {
    }

    public async Task<Host?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Host>(new CommandDefinition(
            $"SELECT {Columns} FROM hosts WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Host>> ListByOwnerAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Host>(new CommandDefinition(
            $"SELECT {Columns} FROM hosts WHERE owner_user_id = @ownerUserId ORDER BY created_at DESC",
            new { ownerUserId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<Host>> SearchAsync(
        string? query, int limit, int offset, CancellationToken ct = default)
    {
        // A blank query lists all; else match a name/label substring (case-insensitive) or an exact host id.
        var trimmed = query?.Trim();
        Guid? id = Guid.TryParse(trimmed, out var parsed) ? parsed : null;

        const string sql = $"""
            SELECT {Columns} FROM hosts
             WHERE (@query IS NULL OR @query = ''
                    OR name ILIKE '%' || @query || '%' OR label ILIKE '%' || @query || '%' OR id = @id)
             ORDER BY created_at DESC, id DESC
             LIMIT @limit OFFSET @offset
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Host>(new CommandDefinition(
            sql,
            new { query = trimmed, id, limit = Math.Max(0, limit), offset = Math.Max(0, offset) },
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT count(*) FROM hosts", cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Host>> ListOnlineAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Host>(new CommandDefinition(
            $"SELECT {Columns} FROM hosts WHERE status = 'online' ORDER BY created_at DESC",
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Host?> GetByAgentTokenHashAsync(string agentTokenHash, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Host>(new CommandDefinition(
            $"SELECT {Columns} FROM hosts WHERE agent_token_hash = @agentTokenHash", new { agentTokenHash },
            cancellationToken: ct));
    }

    public async Task<Host> CreateAsync(Host host, CancellationToken ct = default)
    {
        const string sql = $"""
            INSERT INTO hosts (id, owner_user_id, name, label, status, agent_token_hash, agent_token_prefix,
                               wisp_version, agent_version, max_leases, max_streams, isolation_levels,
                               default_isolation, gpu_classes, gpu_count, last_seen_at, created_at, updated_at)
            VALUES (COALESCE(@Id, gen_random_uuid()), @OwnerUserId, @Name, @Label, @Status::host_status,
                    @AgentTokenHash, @AgentTokenPrefix, @WispVersion, @AgentVersion, @MaxLeases, @MaxStreams,
                    @IsolationLevels::text[], @DefaultIsolation, @GpuClasses::text[], @GpuCount, @LastSeenAt,
                    COALESCE(@CreatedAt, now()), COALESCE(@UpdatedAt, now()))
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleAsync<Host>(new CommandDefinition(sql, ToParameters(host), cancellationToken: ct));
    }

    public async Task<Host> UpdateAsync(Host host, CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE hosts
               SET owner_user_id      = @OwnerUserId,
                   name               = @Name,
                   label              = @Label,
                   status             = @Status::host_status,
                   agent_token_hash   = @AgentTokenHash,
                   agent_token_prefix = @AgentTokenPrefix,
                   wisp_version       = @WispVersion,
                   agent_version      = @AgentVersion,
                   max_leases         = @MaxLeases,
                   max_streams        = @MaxStreams,
                   isolation_levels   = @IsolationLevels::text[],
                   default_isolation  = @DefaultIsolation,
                   gpu_classes        = @GpuClasses::text[],
                   gpu_count          = @GpuCount,
                   last_seen_at       = @LastSeenAt,
                   updated_at         = COALESCE(@UpdatedAt, now())
             WHERE id = @Id
            RETURNING {Columns}
            """;

        await using var conn = await OpenConnectionAsync(ct);
        var updated = await conn.QuerySingleOrDefaultAsync<Host>(
            new CommandDefinition(sql, ToParameters(host), cancellationToken: ct));
        return updated ?? throw new InvalidOperationException($"Host '{host.Id}' does not exist.");
    }

    public async Task<Host?> SetOnlineStateAsync(
        Guid id, HostStatus status, DateTimeOffset? lastSeenAt, DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE hosts
               SET status       = @Status::host_status,
                   last_seen_at = COALESCE(@LastSeenAt, last_seen_at),
                   updated_at   = @UpdatedAt
             WHERE id = @Id
            RETURNING {Columns}
            """;

        var parameters = new
        {
            Id = id,
            Status = PgEnum.ToLabel(status),
            LastSeenAt = lastSeenAt,
            UpdatedAt = updatedAt,
        };

        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Host>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task<Host?> SetAdvertisedIsolationAsync(
        Guid id, IReadOnlyList<string> isolationLevels, string defaultIsolation, DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE hosts
               SET isolation_levels  = @IsolationLevels::text[],
                   default_isolation = @DefaultIsolation,
                   updated_at        = @UpdatedAt
             WHERE id = @Id
            RETURNING {Columns}
            """;

        var parameters = new
        {
            Id = id,
            IsolationLevels = isolationLevels.ToArray(),
            DefaultIsolation = defaultIsolation,
            UpdatedAt = updatedAt,
        };

        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Host>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task<Host?> SetAdvertisedGpuAsync(
        Guid id, IReadOnlyList<string> gpuClasses, int gpuCount, DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE hosts
               SET gpu_classes = @GpuClasses::text[],
                   gpu_count   = @GpuCount,
                   updated_at  = @UpdatedAt
             WHERE id = @Id
            RETURNING {Columns}
            """;

        var parameters = new
        {
            Id = id,
            GpuClasses = gpuClasses.ToArray(),
            GpuCount = gpuCount,
            UpdatedAt = updatedAt,
        };

        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Host>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task<Host?> SetAdvertisedVersionsAndCapacityAsync(
        Guid id, string? wispVersion, string? agentVersion, int? maxLeases, int? maxStreams,
        DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE hosts
               SET wisp_version  = @WispVersion,
                   agent_version = @AgentVersion,
                   max_leases    = @MaxLeases,
                   max_streams   = @MaxStreams,
                   updated_at    = @UpdatedAt
             WHERE id = @Id
            RETURNING {Columns}
            """;

        var parameters = new
        {
            Id = id,
            WispVersion = wispVersion,
            AgentVersion = agentVersion,
            MaxLeases = maxLeases,
            MaxStreams = maxStreams,
            UpdatedAt = updatedAt,
        };

        await using var conn = await OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Host>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM hosts WHERE id = @id", new { id }, cancellationToken: ct));
        return affected > 0;
    }

    /// <summary>Projects a <see cref="Host"/> to SQL parameters, the status enum as its text label.</summary>
    private static object ToParameters(Host host) => new
    {
        Id = host.Id == Guid.Empty ? (Guid?)null : host.Id,
        host.OwnerUserId,
        host.Name,
        host.Label,
        Status = PgEnum.ToLabel(host.Status),
        host.AgentTokenHash,
        host.AgentTokenPrefix,
        host.WispVersion,
        host.AgentVersion,
        host.MaxLeases,
        host.MaxStreams,
        IsolationLevels = host.IsolationLevels.ToArray(),
        host.DefaultIsolation,
        GpuClasses = host.GpuClasses.ToArray(),
        host.GpuCount,
        host.LastSeenAt,
        CreatedAt = host.CreatedAt == default ? (DateTimeOffset?)null : host.CreatedAt,
        UpdatedAt = host.UpdatedAt == default ? (DateTimeOffset?)null : host.UpdatedAt,
    };
}
