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

    /// <summary>
    /// Optional machine-readable failure code (omitted from the wire when the
    /// agent has none). <c>at_capacity</c> = the local wisp rejected the
    /// contract with HTTP 409 (host budgets exhausted) — mapped to the API's
    /// own <c>at_capacity</c> so the race window past the manager-side
    /// fast-fail still surfaces as 409, not a generic 502.
    /// </summary>
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;
}
