using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>lease.accepted</c> (A→W) -- the agent called wisp <c>POST /contracts</c> and got a
/// contract back; provisioning is underway (docs/TUNNEL.md §5). Echoes the request
/// <c>rid</c> (inherited) so Wisper can correlate it with the <c>lease.create</c>.
/// </summary>
public record LeaseAccepted : ControlEnvelope
{
    public LeaseAccepted() => T = FrameTypes.LeaseAccepted;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;

    [JsonPropertyName("wispContractId")]
    public string WispContractId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}
