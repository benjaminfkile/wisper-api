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
}
