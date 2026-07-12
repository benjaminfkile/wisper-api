using Wisper.Api.Tunnel;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// An <see cref="IHostCapabilitySource"/> double: a test declares a host's advertised wisp capability
/// directly (the real source reads it off a live tunnel <c>hello</c>, which Grunt has none of). A host with
/// no entry resolves to <c>null</c> — the "offline" case the host API rejects with <c>host_offline</c>.
/// </summary>
public sealed class FakeHostCapabilitySource : IHostCapabilitySource
{
    private readonly Dictionary<Guid, HostCapabilitySnapshot> _capabilities = new();

    /// <summary>Declares (or replaces) the live capability advertised for <paramref name="hostId"/>.</summary>
    public void Set(Guid hostId, HostCapabilitySnapshot capability) => _capabilities[hostId] = capability;

    /// <summary>Drops <paramref name="hostId"/> — it then reads as offline (no capability).</summary>
    public void Clear(Guid hostId) => _capabilities.Remove(hostId);

    public HostCapabilitySnapshot? GetCapability(Guid hostId) =>
        _capabilities.TryGetValue(hostId, out var capability) ? capability : null;
}
