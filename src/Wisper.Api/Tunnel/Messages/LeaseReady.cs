using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>lease.ready</c> (A→W) -- wisp reached <c>ready</c>; the container is up
/// (docs/TUNNEL.md §5). Unsolicited relative to any single request frame (it carries no
/// <c>rid</c>), so Wisper correlates it by <c>leaseId</c>. Wisper starts the meter here.
/// </summary>
public record LeaseReady : ControlEnvelope
{
    public LeaseReady() => T = FrameTypes.LeaseReady;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;
}
