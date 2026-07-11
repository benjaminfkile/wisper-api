using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>lease.failed</c> (A→W) — provisioning/pull failed; nothing is billed
/// (docs/TUNNEL.md §5). Echoes the request <c>rid</c> (inherited) and the
/// <c>leaseId</c> so Wisper can fail whichever awaiter is outstanding.
/// </summary>
public record LeaseFailed : ControlEnvelope
{
    public LeaseFailed() => T = FrameTypes.LeaseFailed;

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; init; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;
}
