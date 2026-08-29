using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>lease.released</c> (A→W) -- the agent called wisp <c>DELETE</c> and the container is
/// gone (docs/TUNNEL.md §5). Echoes the <c>lease.release</c> <c>rid</c> (inherited).
/// </summary>
public record LeaseReleased : ControlEnvelope
{
    public LeaseReleased() => T = FrameTypes.LeaseReleased;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;
}
