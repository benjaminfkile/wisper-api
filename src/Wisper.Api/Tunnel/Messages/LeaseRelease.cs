using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>lease.release</c> (W→A) -- consumer released / TTL / admin; the agent calls wisp
/// <c>DELETE /contracts/:id</c> (docs/TUNNEL.md §5). Carries the server-assigned
/// <c>rid</c> (inherited) so the <c>lease.released</c> reply can be correlated.
/// </summary>
public record LeaseRelease : ControlEnvelope
{
    public LeaseRelease() => T = FrameTypes.LeaseRelease;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;
}
