using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wisper.Api.Persistence;
using Wisper.Api.Persistence.BillingIncidents;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Task #212 regression: <see cref="PolicyFallbackStore.GetAggregateAsync"/> and
/// <see cref="PolicyFallbackStore.AckAsync"/> driven end to end against a <b>real</b> Postgres
/// server (<see cref="EphemeralPostgres"/>), rather than the in-memory double the rest of the suite
/// uses.
/// <para>
/// The bug this fixture would have caught: once the first ack wrote
/// <c>operational_state.policy_fallback_ack_at</c>, every subsequent aggregate read (and every next
/// ack, which reads first) 500'd. Dapper's scalar path
/// (<c>QuerySingleOrDefaultAsync&lt;DateTimeOffset?&gt;</c>) uses <c>Convert.ChangeType</c>, but
/// Npgsql hands back a <c>DateTime</c> for a <c>timestamptz</c> column, and there is no cross-cast
/// from <c>DateTime</c> to <c>DateTimeOffset</c>. It only worked while the column was NULL. The
/// fix reads the watermark through a typed row class (like every other repository does) so Dapper's
/// property mapper handles the timestamptz -> DateTimeOffset conversion.
/// </para>
/// <para>
/// This stands up a real server, so it is gated behind the explicit <c>WISPER_RUN_PG_TESTS</c>
/// opt-in (task #558). When it is unset -- the default, including normal CI -- the fixture reports
/// unavailable and each test is reported <b>skipped</b> (a visible <c>[SkippableFact]</c> skip, not
/// a hidden no-op) so the suite stays deterministically green regardless of whatever Postgres the
/// runner happens to ship. Set <c>WISPER_RUN_PG_TESTS=1</c> to run it for real (this repo ships
/// PostgreSQL 15); see <see cref="EphemeralPostgres"/> and the README's "Develop" section.
/// </para>
/// </summary>
public sealed class PostgresPolicyFallbackStoreTests
    : IClassFixture<PostgresPolicyFallbackStoreTests.PgFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    private readonly PgFixture _pg;

    public PostgresPolicyFallbackStoreTests(PgFixture pg) => _pg = pg;

    [SkippableFact]
    public async Task Full_record_read_ack_read_re_record_cycle_survives_the_real_schema()
    {
        Skip.IfNot(_pg.Available, PgFixture.SkipReason); // visible skip unless WISPER_RUN_PG_TESTS opted in

        // Every test gets its own operational_state row and clean billing_incidents journal so a
        // second test cannot see the first's ack watermark.
        await ResetAsync(_pg.DataSource!);
        var store = new PolicyFallbackStore(new Db(_pg.DataSource!));

        // Record one fallback of each kind so the aggregate counts across the CHECK domain. The
        // stale event's policy_id must reference a real platform_policy row (FK), so we thread the
        // seeded default policy's id through; the missing_at_flush event carries a null policy_id
        // by definition.
        var stalePolicyId = await GetSeededPolicyIdAsync(_pg.DataSource!);
        var leaseA = Guid.NewGuid();
        var leaseB = Guid.NewGuid();
        await store.RecordAsync(PolicyFallbackKind.StaleFallback, leaseA, stalePolicyId, T0);
        await store.RecordAsync(PolicyFallbackKind.MissingAtFlush, leaseB, null, T0.AddMinutes(1));

        // Aggregate before ack: count includes both rows, last_at wins the newer one, and
        // last_policy_id is null (the newest event is missing_at_flush).
        var beforeAck = await store.GetAggregateAsync();
        Assert.Equal(2, beforeAck.Count);
        Assert.Equal(T0.AddMinutes(1), beforeAck.LastAt);
        Assert.Null(beforeAck.LastPolicyId);
        Assert.Null(beforeAck.AckAt);

        // Ack returns what was cleared and writes the watermark to operational_state. The pre-fix
        // scalar read path (QuerySingleOrDefaultAsync<DateTimeOffset?>) trips on the very next read
        // because the column is no longer NULL; the row-class path handles it.
        var ackAt = T0.AddMinutes(5);
        var acked = await store.AckAsync(ackAt);
        Assert.Equal(2, acked.Count);
        Assert.Equal(T0.AddMinutes(1), acked.LastAt);
        Assert.Null(acked.LastPolicyId);
        Assert.Null(acked.AckAt); // this ack was the first one; no prior watermark surfaced

        // Aggregate after ack: the badge clears (count 0, both nullable fields null) while the
        // journal rows stay intact. The ack watermark rides back on the aggregate for display.
        var afterAck = await store.GetAggregateAsync();
        Assert.Equal(0, afterAck.Count);
        Assert.Null(afterAck.LastAt);
        Assert.Null(afterAck.LastPolicyId);
        Assert.Equal(ackAt, afterAck.AckAt);

        // A fresh fallback after the watermark re-arms the aggregate -- an ack is a badge clear,
        // not a silence. Re-uses the seeded policy id (the FK constraint again requires a real row).
        var freshPolicyId = stalePolicyId;
        await store.RecordAsync(
            PolicyFallbackKind.StaleFallback, Guid.NewGuid(), freshPolicyId, ackAt.AddMinutes(1));

        var afterFresh = await store.GetAggregateAsync();
        Assert.Equal(1, afterFresh.Count);
        Assert.Equal(ackAt.AddMinutes(1), afterFresh.LastAt);
        Assert.Equal(freshPolicyId, afterFresh.LastPolicyId);
        Assert.Equal(ackAt, afterFresh.AckAt);

        // A second ack now reads a NON-NULL watermark from operational_state first (this is the
        // exact path the regression tripped on before the fix) and reports the fresh incident as
        // the cleared aggregate.
        var secondAckAt = ackAt.AddMinutes(10);
        var acked2 = await store.AckAsync(secondAckAt);
        Assert.Equal(1, acked2.Count);
        Assert.Equal(ackAt.AddMinutes(1), acked2.LastAt);
        Assert.Equal(freshPolicyId, acked2.LastPolicyId);
        Assert.Equal(ackAt, acked2.AckAt); // the prior watermark, surfaced back to the caller

        var afterSecondAck = await store.GetAggregateAsync();
        Assert.Equal(0, afterSecondAck.Count);
        Assert.Null(afterSecondAck.LastAt);
        Assert.Null(afterSecondAck.LastPolicyId);
        Assert.Equal(secondAckAt, afterSecondAck.AckAt);
    }

    [SkippableFact]
    public async Task Ack_watermark_is_strict_greater_than_so_a_boundary_event_stays_acknowledged()
    {
        Skip.IfNot(_pg.Available, PgFixture.SkipReason);

        // Task #210: the SQL predicate is occurred_at > ack_at (strict), so a row recorded at
        // EXACTLY the ack watermark must NOT surface after the ack. Locked down here on the real
        // schema so the in-memory double and the SQL store cannot drift.
        await ResetAsync(_pg.DataSource!);
        var store = new PolicyFallbackStore(new Db(_pg.DataSource!));
        var policyId = await GetSeededPolicyIdAsync(_pg.DataSource!);
        await store.RecordAsync(PolicyFallbackKind.StaleFallback, Guid.NewGuid(), policyId, T0);

        await store.AckAsync(T0);

        var aggregate = await store.GetAggregateAsync();
        Assert.Equal(0, aggregate.Count);
        Assert.Equal(T0, aggregate.AckAt);
    }

    /// <summary>Truncates the two tables the store touches and re-seeds the single operational_state row.</summary>
    private static async Task ResetAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("TRUNCATE billing_incidents");
        await conn.ExecuteAsync("DELETE FROM operational_state");
        await conn.ExecuteAsync("INSERT INTO operational_state (id) VALUES (1)");
    }

    /// <summary>Returns the id of the platform_policy row migration 0017 seeds on a fresh DB.</summary>
    private static async Task<Guid> GetSeededPolicyIdAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.QuerySingleAsync<Guid>(
            "SELECT id FROM platform_policy ORDER BY effective_from ASC LIMIT 1");
    }

    /// <summary>
    /// Class fixture: stands up one throwaway server and runs the migrations once for the whole
    /// class. Unless the <c>WISPER_RUN_PG_TESTS</c> opt-in is set it reports
    /// <see cref="Available"/> = <c>false</c> and the tests report a visible skip.
    /// </summary>
    public sealed class PgFixture : IAsyncLifetime
    {
        public const string SkipReason =
            "ephemeral-Postgres regression is opt-in: set WISPER_RUN_PG_TESTS=1 to run it (task #558)";

        private EphemeralPostgres? _server;

        public NpgsqlDataSource? DataSource { get; private set; }

        public bool Available => DataSource is not null;

        public async Task InitializeAsync()
        {
            _server = await EphemeralPostgres.TryStartAsync();
            if (_server is null)
            {
                return;
            }

            // The Dapper snake_case/PascalCase mapping the repositories rely on (set at app wiring).
            DefaultTypeMap.MatchNamesWithUnderscores = true;

            MigrationRunner.Run(_server.ConnectionString, NullLogger.Instance);
            DataSource = new NpgsqlDataSourceBuilder(_server.ConnectionString).Build();
        }

        public async Task DisposeAsync()
        {
            if (DataSource is not null)
            {
                await DataSource.DisposeAsync();
            }

            if (_server is not null)
            {
                await _server.DisposeAsync();
            }
        }
    }
}
