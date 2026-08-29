using System.Collections.Concurrent;

namespace Wisper.Api.Persistence;

/// <summary>
/// Shared base for in-memory repository doubles used by unit tests (Grunt has no Postgres, so the
/// real Dapper repositories cannot run here). Provides a thread-safe, key-indexed store plus the
/// common lookups a concrete double needs; the double implements its entity-specific
/// <see cref="IRepository"/> interface in terms of these helpers. Semantics deliberately mirror the
/// SQL side -- <see cref="Insert"/> rejects a duplicate key the way a primary-key constraint would.
/// </summary>
/// <typeparam name="TId">The entity's identity type.</typeparam>
/// <typeparam name="TEntity">The stored entity type.</typeparam>
public abstract class InMemoryRepositoryBase<TId, TEntity> : IRepository
    where TId : notnull
    where TEntity : class
{
    private readonly ConcurrentDictionary<TId, TEntity> _items = new();

    /// <summary>Extracts the identity used to key <paramref name="entity"/> in the store.</summary>
    protected abstract TId KeyOf(TEntity entity);

    /// <summary>Number of stored entities.</summary>
    protected int Count => _items.Count;

    /// <summary>Inserts <paramref name="entity"/>; throws if its key already exists (PK conflict).</summary>
    protected void Insert(TEntity entity)
    {
        if (!_items.TryAdd(KeyOf(entity), entity))
        {
            throw new InvalidOperationException($"An entity with key '{KeyOf(entity)}' already exists.");
        }
    }

    /// <summary>Inserts or replaces <paramref name="entity"/> by its key.</summary>
    protected void Upsert(TEntity entity) => _items[KeyOf(entity)] = entity;

    /// <summary>Finds an entity by id, or <c>null</c> if absent.</summary>
    protected TEntity? Find(TId id) => _items.TryGetValue(id, out var found) ? found : null;

    /// <summary>Finds the first entity matching <paramref name="predicate"/>, or <c>null</c>.</summary>
    protected TEntity? FindBy(Func<TEntity, bool> predicate) => _items.Values.FirstOrDefault(predicate);

    /// <summary>All entities matching <paramref name="predicate"/>.</summary>
    protected IReadOnlyList<TEntity> Where(Func<TEntity, bool> predicate) =>
        _items.Values.Where(predicate).ToList();

    /// <summary>A snapshot of every stored entity.</summary>
    protected IReadOnlyList<TEntity> All() => _items.Values.ToArray();

    /// <summary>Removes the entity with <paramref name="id"/>; <c>true</c> if one was removed.</summary>
    protected bool Remove(TId id) => _items.TryRemove(id, out _);

    /// <summary>Empties the store (useful for per-test isolation).</summary>
    protected void Clear() => _items.Clear();
}
