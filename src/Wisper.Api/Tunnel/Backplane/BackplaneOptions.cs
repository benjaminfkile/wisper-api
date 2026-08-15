namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Multi-instance backplane configuration (docs/DESIGN.md §7, docs/TUNNEL.md §11), bound from the
/// <see cref="SectionName"/> config section. When <see cref="Enabled"/> is <c>false</c> (the default)
/// the manager runs single-instance with the in-memory registry/relay and never touches Redis. When
/// enabled, host presence and cross-instance relay routing flow over the pub/sub backplane so a host
/// tunnel pinned to one instance can be driven from any other.
/// </summary>
public sealed class BackplaneOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Tunnel:Backplane";

    /// <summary>
    /// Turns on the distributed backplane. Off by default — a single manager instance needs no Redis
    /// (docs/DESIGN.md §7: "Not required until &gt;1 instance runs"). The registry/relay interfaces are
    /// unchanged either way, so callers are oblivious to which mode is active.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Stable identifier for this manager instance — the key hosts' presence records point at and the
    /// address its RPC request/reply channels are named after. When blank a random id is generated at
    /// startup (fine for autoscaled pods, which are ephemeral).
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// StackExchange.Redis connection string. When set (and <see cref="Enabled"/>), a real Redis
    /// backplane + presence store are used; when blank the in-process loopback backplane is used
    /// instead (single-process dev/testing — no Redis required to build or run).
    /// </summary>
    public string? RedisConfiguration { get; set; }

    /// <summary>Prefix for every Redis key/channel this backplane owns. Keeps a shared Redis tidy.</summary>
    public string ChannelPrefix { get; set; } = "wisper";

    /// <summary>
    /// How long (ms) an instance waits for the owning instance to reply to a routed relay request
    /// before failing with <c>upstream_timeout</c>. Defaults to the tunnel relay deadline so a routed
    /// call is bounded the same as a local one.
    /// </summary>
    public int RpcTimeoutMs { get; set; } = 120000;

    /// <summary>
    /// TTL applied to each Redis degraded entry, refreshed by every degraded heartbeat (task #65).
    /// Ensures a host whose instance crashed (Redis mode: the disconnect handler never runs) does not
    /// leave a stuck-degraded entry behind forever, while sized generously enough — many multiples of
    /// the heartbeat interval — that a live degraded host is refreshed long before this expires, so it
    /// never flaps healthy from TTL alone. Default 600s (~20× the 30s heartbeat cadence and ~8× the
    /// 75s default liveness timeout — a live degraded host that gets its tunnel closed on liveness has
    /// its entry cleared by the disconnect path long before this hits).
    /// </summary>
    public int DegradedTtlSeconds { get; set; } = 600;
}
