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
/// <see cref="IHostDegradedStore"/> on transition and logs each transition exactly once — a healthy
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
        // The tunnel is up, but the agent reported "degraded" — the shared set must carry the host
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
        // automatically — the second acceptance criterion for task #62.
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
        // Steady-state degraded (many beats reporting the same status) MUST NOT flood the log — the
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
        // — the "log once per transition" rule is per transition, not per direction.
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
        // we don't yet know) counts as healthy — an older agent (no status field) MUST never end up
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
        // The applier is fail-safe on its own — a store hiccup must never disturb lease reconciliation
        // or the tunnel. On failure the connection flag is NOT flipped (so the next beat retries) and
        // the log carries the exception.
        var fx = Fixture.Create(store: new ThrowingDegradedStore());

        await HeartbeatDegradedApply.ApplyAsync(
            fx.Connection, Heartbeat("degraded"), fx.Store, fx.Logger, ct: default);

        Assert.False(fx.Connection.IsDegraded);
        Assert.Equal(1, fx.Logger.ErrorCount);
    }

    // ----- fixture / helpers ---------------------------------------------------------------------

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

    /// <summary>An <see cref="IHostDegradedStore"/> that throws on every write — for the fail-safe test.</summary>
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
