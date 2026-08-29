using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Task #62: the manager must honor the agent's self-reported <c>"degraded"</c> in
/// <c>host.heartbeat</c>. These tests cover the applier that flips the shared
/// <see cref="IHostDegradedStore"/> on transition and logs each transition exactly once -- a healthy
/// agent that stays degraded through many beats never floods the log, and a subsequent healthy
/// beat both clears the shared flag and emits the recovery line. The end-to-end placement side is
/// covered by the catalog and lease-admission tests separately.
/// </summary>
public class HeartbeatDegradedApplyTests
{
    private static readonly Guid HostId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ----- wire ----------------------------------------------------------------------------------

    [Fact]
    public void Heartbeat_carrying_status_degraded_is_parsed()
    {
        // The wire contract: the agent's self-reported status rides on the heartbeat as a top-level
        // string; older agents omit it and deserialize to null (round-trip fidelity).
        const string json = "{\"t\":\"host.heartbeat\",\"leases\":[],\"status\":\"degraded\"}";

        var heartbeat = ControlJson.Deserialize<HostHeartbeat>(json);

        Assert.NotNull(heartbeat);
        Assert.Equal("degraded", heartbeat!.Status);
    }

    [Fact]
    public void Heartbeat_without_status_leaves_status_null()
    {
        const string json = "{\"t\":\"host.heartbeat\",\"leases\":[]}";

        var heartbeat = ControlJson.Deserialize<HostHeartbeat>(json);

        Assert.NotNull(heartbeat);
        Assert.Null(heartbeat!.Status);
    }

    // ----- applier -------------------------------------------------------------------------------

    [Fact]
    public async Task Degraded_heartbeat_adds_the_host_to_the_shared_store()
    {
        // The tunnel is up, but the agent reported "degraded" -- the shared set must carry the host
        // so every instance's placement path (catalog liveness / lease admission) excludes it.
        var fx = Fixture.Create();

        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat("degraded"), fx.Store, fx.Logger, ct: default);

        Assert.True(await fx.Store.IsDegradedAsync(HostId.ToString()));
        Assert.True(fx.Connection.IsDegraded);
    }

    [Fact]
    public async Task Non_degraded_heartbeat_after_degraded_clears_the_shared_store()
    {
        // A subsequent heartbeat with no status (or any non-degraded value) restores placement
        // automatically -- the second acceptance criterion for task #62.
        var fx = Fixture.Create();
        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat("degraded"), fx.Store, fx.Logger, ct: default);
        Assert.True(await fx.Store.IsDegradedAsync(HostId.ToString()));

        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat(status: null), fx.Store, fx.Logger, ct: default);

        Assert.False(await fx.Store.IsDegradedAsync(HostId.ToString()));
        Assert.False(fx.Connection.IsDegraded);
    }

    [Fact]
    public async Task Repeated_degraded_beats_log_only_the_first_transition()
    {
        // Steady-state degraded (many beats reporting the same status) MUST NOT flood the log -- the
        // handler is a no-op on unchanged state, so both the store write and the log line fire only
        // on the transition. AC #213.
        var fx = Fixture.Create();

        for (var i = 0; i < 5; i++)
        {
            await HeartbeatDegradedApply.ApplyAsync(
                fx.Connection, Heartbeat("degraded"), fx.Store, fx.Logger, ct: default);
        }

        Assert.Equal(1, fx.Logger.WarningCount);
        Assert.Equal(0, fx.Logger.InformationCount);
    }

    [Fact]
    public async Task Repeated_healthy_beats_after_recovery_log_only_the_recovery_line()
    {
        // The recovery transition logs once; every subsequent healthy beat is a no-op. Combined with
        // the previous test this proves "log once per transition, never per heartbeat" (AC #213).
        var fx = Fixture.Create();
        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat("degraded"), fx.Store, fx.Logger, ct: default);

        for (var i = 0; i < 5; i++)
        {
            await HeartbeatDegradedApply.ApplyAsync(
                fx.Connection, Heartbeat(status: null), fx.Store, fx.Logger, ct: default);
        }

        Assert.Equal(1, fx.Logger.WarningCount);       // the original degraded transition
        Assert.Equal(1, fx.Logger.InformationCount);   // and the single recovery transition
    }

    [Fact]
    public async Task Alternating_transitions_each_log_and_toggle_the_store()
    {
        // A flapping agent MUST log each transition (both directions) so operators can see the flap
        // -- the "log once per transition" rule is per transition, not per direction.
        var fx = Fixture.Create();

        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat("degraded"), fx.Store, fx.Logger, ct: default);
        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat(status: null), fx.Store, fx.Logger, ct: default);
        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat("degraded"), fx.Store, fx.Logger, ct: default);
        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat(status: null), fx.Store, fx.Logger, ct: default);

        Assert.Equal(2, fx.Logger.WarningCount);       // two enters degraded
        Assert.Equal(2, fx.Logger.InformationCount);   // two recoveries
        Assert.False(await fx.Store.IsDegradedAsync(HostId.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("healthy")]
    [InlineData("ok")]
    [InlineData("unknown-future-value")]
    public async Task Unknown_or_absent_status_normalises_to_healthy(string? status)
    {
        // The docs say only exactly "degraded" excludes; any other value (including a future value
        // we don't yet know) counts as healthy -- an older agent (no status field) MUST never end up
        // stranded out of placement.
        var fx = Fixture.Create();

        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat(status), fx.Store, fx.Logger, ct: default);

        Assert.False(await fx.Store.IsDegradedAsync(HostId.ToString()));
        Assert.False(fx.Connection.IsDegraded);
        Assert.Equal(0, fx.Logger.WarningCount);
        Assert.Equal(0, fx.Logger.InformationCount);
    }

    [Fact]
    public async Task Degraded_status_is_case_insensitive()
    {
        // The applier trims + case-folds so a slightly-differently-cased "Degraded" from a future
        // agent build still counts. This keeps the wire tolerant without treating truly unknown
        // labels as degraded.
        var fx = Fixture.Create();

        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat(" DEGRADED "), fx.Store, fx.Logger, ct: default);

        Assert.True(await fx.Store.IsDegradedAsync(HostId.ToString()));
    }

    [Fact]
    public async Task Store_write_failure_is_swallowed_and_logged_and_leaves_state_unchanged()
    {
        // The applier is fail-safe on its own -- a store hiccup must never disturb lease reconciliation
        // or the tunnel. On failure the connection flag is NOT flipped (so the next beat retries) and
        // the log carries the exception.
        var fx = Fixture.Create(store: new ThrowingDegradedStore());

        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat("degraded"), fx.Store, fx.Logger, ct: default);

        Assert.False(fx.Connection.IsDegraded);
        Assert.Equal(1, fx.Logger.ErrorCount);
    }

    // ----- task #65: settle on first heartbeat --------------------------------------------------

    [Fact]
    public async Task First_healthy_heartbeat_clears_a_stale_degraded_entry_left_by_a_prior_connection()
    {
        // AC #223: a stale entry in the shared store -- from a superseded/crashed prior connection, or a
        // disconnect-time clear that lost a race -- must be cleared by the FIRST heartbeat of the fresh
        // (healthy) connection. Without the unconditional first-beat settle the guard would skip the
        // write on every steady-state healthy beat (fresh IsDegraded=false == reportedDegraded=false)
        // and the host would stay excluded from placement forever.
        var fx = Fixture.Create();
        await fx.Store.SetDegradedAsync(HostId.ToString()); // seed the stale entry

        // Fresh connection -- IsDegradedSettled starts false -- receives one healthy heartbeat.
        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat(status: null), fx.Store, fx.Logger, ct: default);

        Assert.False(await fx.Store.IsDegradedAsync(HostId.ToString()));
        Assert.False(fx.Connection.IsDegraded);
        Assert.True(fx.Connection.IsDegradedSettled);
    }

    [Fact]
    public async Task Supersede_while_degraded_does_not_strand_the_host_after_healthy_reconnect()
    {
        // AC #224: a supersede while degraded is the common agent-reconnect path -- the prior tunnel's
        // disconnect handler deliberately skips its ClearDegradedAsync on supersede so the newer
        // owner's own heartbeats govern the flag. If the new agent comes back HEALTHY, the first
        // heartbeat of the new connection MUST clear the stale entry. Same shared store across both
        // connections (as in the shared-Redis production shape).
        var sharedStore = new InMemoryHostDegradedStore();

        var oldConn = MakeConnection("sess-old");
        // Old connection reports degraded -- this is what a real deployment would look like right
        // before the supersede: the store carries the host, and the disconnect path leaves it alone.
        await HeartbeatDegradedApply.ApplyAsync(
            oldConn, Heartbeat("degraded"), sharedStore, NullLogger.Instance, ct: default);
        Assert.True(await sharedStore.IsDegradedAsync(HostId.ToString()));

        // New agent reconnects (supersede) -- fresh connection, healthy heartbeats. This is the leak.
        var newConn = MakeConnection("sess-new");
        var logger = new CountingLogger();

        await HeartbeatDegradedApply.ApplyAsync(
            newConn, Heartbeat(status: null), sharedStore, logger, ct: default);

        Assert.False(await sharedStore.IsDegradedAsync(HostId.ToString()));
        Assert.False(newConn.IsDegraded);
        Assert.True(newConn.IsDegradedSettled);
    }

    [Fact]
    public async Task First_healthy_beat_without_any_stale_entry_does_not_log_a_recovery_line()
    {
        // First-beat settle writes the store authoritatively (a redundant DEL when nothing is there),
        // but it must NOT emit the recovery info log line on a non-transition -- that line is reserved
        // for a genuine degraded → healthy transition on this connection. Preserves AC #225.
        var fx = Fixture.Create();

        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat(status: null), fx.Store, fx.Logger, ct: default);

        Assert.False(await fx.Store.IsDegradedAsync(HostId.ToString()));
        Assert.Equal(0, fx.Logger.InformationCount);
        Assert.Equal(0, fx.Logger.WarningCount);
        Assert.True(fx.Connection.IsDegradedSettled);
    }

    [Fact]
    public async Task Repeated_healthy_beats_after_first_settle_touch_the_store_at_most_once()
    {
        // Once the first-beat settle has completed, subsequent healthy beats fall through the
        // healthy-steady-state fast-path with zero store writes and zero log lines (AC #225).
        var recording = new RecordingDegradedStore();
        var fx = Fixture.Create(store: recording);

        for (var i = 0; i < 10; i++)
        {
            await HeartbeatDegradedApply.ApplyAsync(
                fx.Connection, Heartbeat(status: null), fx.Store, fx.Logger, ct: default);
        }

        Assert.Equal(1, recording.ClearCount); // exactly one from the first-beat settle
        Assert.Equal(0, recording.SetCount);
        Assert.Equal(0, fx.Logger.InformationCount);
        Assert.Equal(0, fx.Logger.WarningCount);
    }

    [Fact]
    public async Task Every_degraded_beat_writes_the_store_so_a_live_degraded_host_refreshes_its_ttl()
    {
        // AC #226 (in-memory proxy): the Redis store gives its per-host key a TTL on every SET, so a
        // live degraded host that keeps heartbeating must never expire from TTL alone. Modeled here by
        // asserting the applier calls SetDegradedAsync on EVERY degraded heartbeat, not just on the
        // transition -- the Redis store's SET semantics turn each call into an atomic TTL refresh.
        var recording = new RecordingDegradedStore();
        var fx = Fixture.Create(store: recording);

        for (var i = 0; i < 5; i++)
        {
            await HeartbeatDegradedApply.ApplyAsync(
                fx.Connection, Heartbeat("degraded"), fx.Store, fx.Logger, ct: default);
        }

        Assert.Equal(5, recording.SetCount);
        Assert.Equal(1, fx.Logger.WarningCount); // still logged once per transition
        Assert.Equal(0, fx.Logger.InformationCount);
    }

    [Fact]
    public async Task First_beat_settle_failure_does_not_latch_and_retries_next_beat()
    {
        // If the very first beat's store write throws, the connection MUST NOT be marked "settled" --
        // otherwise the next healthy beat would fall into the fast-path and never retry, leaving any
        // stale entry uncleared forever.
        var flakyStore = new FlakyDegradedStore();
        var connection = MakeConnection("sess-flaky");
        var logger = new CountingLogger();

        flakyStore.ThrowOnce = true;
        await HeartbeatDegradedApply.ApplyAsync(
            connection, Heartbeat(status: null), flakyStore, logger, ct: default);

        Assert.False(connection.IsDegradedSettled);
        Assert.Equal(1, logger.ErrorCount);

        // Second beat: store recovers, settle succeeds.
        await HeartbeatDegradedApply.ApplyAsync(
            connection, Heartbeat(status: null), flakyStore, logger, ct: default);

        Assert.True(connection.IsDegradedSettled);
        Assert.Equal(2, flakyStore.ClearCount); // once (threw), once (succeeded)
    }

    // ----- fixture / helpers ---------------------------------------------------------------------

    private static TunnelConnection MakeConnection(string sessionId) => new(
        new StubWebSocket(), HostId.ToString(), sessionId: sessionId,
        maxReceiveBytes: 65536, NullLogger.Instance);

    private static HostHeartbeat Heartbeat(string? status) => new()
    {
        Leases = Array.Empty<HeartbeatLease>(),
        Status = status,
    };

    private sealed class Fixture
    {
        public IHostDegradedStore Store { get; }
        public CountingLogger Logger { get; } = new();
        public TunnelConnection Connection { get; }

        private Fixture(IHostDegradedStore store)
        {
            Store = store;
            Connection = new TunnelConnection(
                new StubWebSocket(), HostId.ToString(), sessionId: "sess-#62",
                maxReceiveBytes: 65536, NullLogger.Instance);
        }

        public static Fixture Create(IHostDegradedStore? store = null) =>
            new(store ?? new InMemoryHostDegradedStore());
    }

    /// <summary>Counts log lines by level so tests can assert "logged exactly once per transition."</summary>
    private sealed class CountingLogger : ILogger
    {
        public int InformationCount { get; private set; }
        public int WarningCount { get; private set; }
        public int ErrorCount { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            switch (logLevel)
            {
                case LogLevel.Information: InformationCount++; break;
                case LogLevel.Warning: WarningCount++; break;
                case LogLevel.Error: ErrorCount++; break;
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>An <see cref="IHostDegradedStore"/> that throws on every write -- for the fail-safe test.</summary>
    private sealed class ThrowingDegradedStore : IHostDegradedStore
    {
        public Task SetDegradedAsync(string hostId, CancellationToken ct = default) =>
            throw new InvalidOperationException("shared store down");
        public Task ClearDegradedAsync(string hostId, CancellationToken ct = default) =>
            throw new InvalidOperationException("shared store down");
        public Task<bool> IsDegradedAsync(string hostId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<IReadOnlyCollection<string>> SnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
    }

    /// <summary>An <see cref="IHostDegradedStore"/> that counts Set/Clear calls so tests can assert
    /// the applier hits the store exactly the expected number of times (TTL-refresh proof).</summary>
    private sealed class RecordingDegradedStore : IHostDegradedStore
    {
        private readonly HashSet<string> _members = new(StringComparer.Ordinal);
        public int SetCount { get; private set; }
        public int ClearCount { get; private set; }

        public Task SetDegradedAsync(string hostId, CancellationToken ct = default)
        {
            SetCount++;
            _members.Add(hostId);
            return Task.CompletedTask;
        }

        public Task ClearDegradedAsync(string hostId, CancellationToken ct = default)
        {
            ClearCount++;
            _members.Remove(hostId);
            return Task.CompletedTask;
        }

        public Task<bool> IsDegradedAsync(string hostId, CancellationToken ct = default) =>
            Task.FromResult(_members.Contains(hostId));

        public Task<IReadOnlyCollection<string>> SnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(_members.ToArray());
    }

    /// <summary>An <see cref="IHostDegradedStore"/> that throws the next write when <see cref="ThrowOnce"/>
    /// is set -- used to prove the first-beat settle does not latch on failure.</summary>
    private sealed class FlakyDegradedStore : IHostDegradedStore
    {
        public bool ThrowOnce { get; set; }
        public int SetCount { get; private set; }
        public int ClearCount { get; private set; }

        public Task SetDegradedAsync(string hostId, CancellationToken ct = default)
        {
            SetCount++;
            if (ThrowOnce)
            {
                ThrowOnce = false;
                throw new InvalidOperationException("shared store hiccup");
            }
            return Task.CompletedTask;
        }

        public Task ClearDegradedAsync(string hostId, CancellationToken ct = default)
        {
            ClearCount++;
            if (ThrowOnce)
            {
                ThrowOnce = false;
                throw new InvalidOperationException("shared store hiccup");
            }
            return Task.CompletedTask;
        }

        public Task<bool> IsDegradedAsync(string hostId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyCollection<string>> SnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
    }

    /// <summary>A do-nothing <see cref="WebSocket"/>: the applier never touches the socket.</summary>
    private sealed class StubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken ct) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
