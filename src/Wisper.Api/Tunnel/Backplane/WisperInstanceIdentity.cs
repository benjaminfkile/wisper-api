namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// The stable id of this manager instance in the backplane (docs/DESIGN.md §7). Presence records
/// map <c>host → instanceId</c>, and each instance's RPC request/reply channels are named after it,
/// so an instance can address the one that owns a given host's tunnel.
/// </summary>
public sealed class WisperInstanceIdentity
{
    public WisperInstanceIdentity(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            throw new ArgumentException("instance id must be non-empty", nameof(instanceId));
        }

        InstanceId = instanceId;
    }

    /// <summary>This instance's id.</summary>
    public string InstanceId { get; }
}
