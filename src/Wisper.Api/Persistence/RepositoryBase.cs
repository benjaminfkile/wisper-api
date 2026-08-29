using Npgsql;

namespace Wisper.Api.Persistence;

/// <summary>
/// Base for Dapper + explicit-SQL repositories (docs/DATA_MODEL.md §1). Holds the shared
/// <see cref="Db"/> and hands subclasses pooled connections; subclasses own their SQL. No generic
/// CRUD/ORM surface is offered on purpose -- a financial schema with constraint triggers and
/// hand-tuned transactions wants reviewable SQL per query, not query generation.
/// </summary>
public abstract class RepositoryBase : IRepository
{
    protected RepositoryBase(Db db) => Db = db;

    /// <summary>The shared database handle.</summary>
    protected Db Db { get; }

    /// <summary>Opens a pooled connection for a query. Throws when no database is configured.</summary>
    protected ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default) =>
        Db.OpenConnectionAsync(ct);
}
