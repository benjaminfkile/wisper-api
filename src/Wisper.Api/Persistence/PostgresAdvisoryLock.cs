using Dapper;
using Npgsql;

namespace Wisper.Api.Persistence;

/// <summary>
/// A lightweight wrapper over PostgreSQL <b>session-scope advisory locks</b> (<c>pg_try_advisory_lock</c> /
/// <c>pg_advisory_unlock</c>). The scheduled background loops (ledger reconciliation, idempotency sweep)
/// use it to serialize multi-instance work without a leader-election dependency: each instance tries the
/// lock when its tick fires; the winner runs the pass, everyone else skips this tick. The lock is
/// session-scoped, so it releases automatically if the connection drops (a crash will not wedge the loop).
/// </summary>
/// <remarks>
/// Advisory locks are keyed by an application-chosen <c>bigint</c>; we allocate a stable id per background
/// loop below. A single connection is held for the duration of the pass; the lock is released explicitly
/// on <see cref="ReleaseAsync"/> and, defensively, when the connection disposes.
/// </remarks>
public sealed class PostgresAdvisoryLock : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly long _key;
    private bool _released;

    private PostgresAdvisoryLock(NpgsqlConnection connection, long key)
    {
        _connection = connection;
        _key = key;
    }

    /// <summary>
    /// Tries to acquire the advisory lock keyed by <paramref name="key"/>. Returns a live handle on success
    /// (the caller runs its critical section, then disposes to release), or <c>null</c> if another session
    /// holds the lock (in which case the caller must skip this pass). Never blocks.
    /// </summary>
    public static async Task<PostgresAdvisoryLock?> TryAcquireAsync(
        Db db, long key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!db.IsConfigured)
        {
            return null;
        }

        var connection = await db.OpenConnectionAsync(ct);
        try
        {
            var acquired = await connection.ExecuteScalarAsync<bool>(
                "SELECT pg_try_advisory_lock(@Key);", new { Key = key });
            if (!acquired)
            {
                await connection.DisposeAsync();
                return null;
            }

            return new PostgresAdvisoryLock(connection, key);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Releases the lock. Idempotent: a second call is a no-op.</summary>
    public async ValueTask ReleaseAsync()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        try
        {
            await _connection.ExecuteScalarAsync<bool>(
                "SELECT pg_advisory_unlock(@Key);", new { Key = _key });
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => ReleaseAsync();

    /// <summary>
    /// Stable advisory-lock keys per background loop. Handpicked constants (docs/DATA_MODEL.md §14): the
    /// only requirement is that two loops do not collide, and that a given loop's key stays constant
    /// across deploys so a rolling restart cannot hand the lock back and forth for the same pass.
    /// </summary>
    public static class Keys
    {
        /// <summary>Advisory-lock key for <c>LedgerReconcileHostedService</c>.</summary>
        public const long LedgerReconcile = 0x77697370_6c65646eL; // "wisp"+"ledn"

        /// <summary>Advisory-lock key for <c>IdempotencySweepHostedService</c>.</summary>
        public const long IdempotencySweep = 0x77697370_6964656dL; // "wisp"+"idem"
    }
}
