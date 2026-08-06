using Wisper.Api.Domain;

namespace Wisper.Api.Tunnel;

/// <summary>
/// <see cref="IHostCapabilitySource"/> over the live tunnel <see cref="IHostRegistry"/>: reads the
/// <c>hello.capability</c> stored on a host's current <see cref="TunnelConnection"/> (docs/TUNNEL.md §5) and
/// flattens it into a <see cref="HostCapabilitySnapshot"/>. A host with no live tunnel (or one whose hello
/// carried no capability) resolves to <c>null</c>, so the host API treats it as offline for allow-list
/// validation (docs/API.md §6). The registry keys on the host id's <c>Guid.ToString()</c> form.
/// </summary>
public sealed class RegistryHostCapabilitySource : IHostCapabilitySource
{
    private readonly IHostRegistry _registry;

    public RegistryHostCapabilitySource(IHostRegistry registry) => _registry = registry;

    public HostCapabilitySnapshot? GetCapability(Guid hostId)
    {
        if (!_registry.TryGet(hostId.ToString(), out var connection) ||
            connection?.Capability is not { } capability)
        {
            return null;
        }

        var limits = capability.Limits;
        var networks = new List<NetworkMode>(limits.Networks.Count);
        foreach (var label in limits.Networks)
        {
            // Drop network labels a newer agent may advertise that this manager does not yet model (§4).
            if (Enum.TryParse<NetworkMode>(label, ignoreCase: true, out var mode) && Enum.IsDefined(mode))
            {
                networks.Add(mode);
            }
        }

        // The advertised GPU facts a later task validates a GPU lease against (task #521): the per-lease
        // device ceiling and the distinct opaque device classes. Absent gpu block ⇒ 0 / empty (no GPU).
        var gpu = capability.Gpu;

        return new HostCapabilitySnapshot(
            capability.Images,
            networks,
            limits.MaxTtlSeconds,
            limits.MaxCpus,
            limits.MaxMemoryMb,
            limits.PidsLimit,
            // The host's advertised container OS ("linux"|"windows"), or null for an older agent that
            // did not send it — surfacing only, back-compatible (docs/TUNNEL.md §5).
            capability.Os,
            MaxGpus: gpu?.MaxGpus ?? 0,
            GpuClasses: HostGpu.NormalizeClasses(gpu?.DeviceClasses));
    }
}
