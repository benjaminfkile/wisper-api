using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>lease.ended</c> (A→W, <b>unsolicited</b>) -- wisp's local reaper/TTL ended the lease
/// on the host (docs/TUNNEL.md §5). Carries no <c>rid</c>; correlated by <c>leaseId</c>.
/// </summary>
public record LeaseEnded : ControlEnvelope
{
    public LeaseEnded() => T = FrameTypes.LeaseEnded;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;

    /// <summary>Why the lease ended, e.g. <c>expired</c>, <c>failed</c>, <c>gone</c>.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}
