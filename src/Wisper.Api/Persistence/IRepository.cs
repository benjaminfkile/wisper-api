namespace Wisper.Api.Persistence;

/// <summary>
/// Marker for a data-access repository. Every persistent aggregate (users, hosts, leases,
/// ledger, …) is reached through an interface that extends this one, with two implementations:
/// a Dapper + explicit-SQL implementation over <see cref="RepositoryBase"/> (docs/DATA_MODEL.md §1),
/// and an in-memory double over <see cref="InMemoryRepositoryBase{TId,TEntity}"/> for unit tests that
/// run without a live database. Keeping the surface behind interfaces is what lets the test suite
/// build and pass on Grunt, which has no Postgres.
/// </summary>
public interface IRepository;
