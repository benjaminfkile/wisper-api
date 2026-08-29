using Npgsql;

namespace Wisper.Api.Persistence;

/// <summary>
/// The service's single entry point to PostgreSQL (docs/DATA_MODEL.md §1). Wraps a pooled
/// <see cref="NpgsqlDataSource"/> built once from the <c>ConnectionStrings:Wisper</c> connection
/// string (env <c>ConnectionStrings__Wisper</c>). Registered as a singleton.
/// </summary>
/// <remarks>
/// Grunt/dev may run with <b>no</b> database configured (the tunnel needs no DB). In that case
/// <see cref="IsConfigured"/> is <c>false</c> and <see cref="DataSource"/> throws -- callers that
/// need the DB must degrade gracefully (see the DB health probe and the migration runner), so the
/// app still boots for the tunnel. Repositories are unit-tested against in-memory doubles, never
/// this type, so no live database is required to build or test.
/// </remarks>
public sealed class Db : IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    /// <summary>Creates a configured <see cref="Db"/> over an already-built pooled data source.</summary>
    public Db(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    private Db() => _dataSource = null;

    /// <summary>A <see cref="Db"/> with no database configured (dev/tunnel-only or unit tests).</summary>
    public static Db Unconfigured { get; } = new();

    /// <summary>Whether a connection string was supplied and a pooled data source exists.</summary>
    public bool IsConfigured => _dataSource is not null;

    /// <summary>
    /// The pooled data source. Throws <see cref="InvalidOperationException"/> when no database is
    /// configured -- guard with <see cref="IsConfigured"/> (or <see cref="TryGetDataSource"/>) on
    /// paths that must tolerate a DB-less boot.
    /// </summary>
    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException(
            "No database is configured. Set ConnectionStrings:Wisper (env ConnectionStrings__Wisper).");

    /// <summary>Non-throwing accessor for the data source; <c>false</c> when unconfigured.</summary>
    public bool TryGetDataSource(out NpgsqlDataSource dataSource)
    {
        dataSource = _dataSource!;
        return _dataSource is not null;
    }

    /// <summary>Opens a pooled connection. Throws when no database is configured.</summary>
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default) =>
        DataSource.OpenConnectionAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }
}
