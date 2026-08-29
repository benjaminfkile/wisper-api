using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel.Messages;

/// <summary>
/// <c>error</c> (either direction) -- a generic typed failure for a request or stream
/// (docs/TUNNEL.md §5, §12). Optionally echoes the <c>rid</c>/<c>sid</c> of the frame it
/// refers to (inherited from <see cref="ControlEnvelope"/>, omitted when 0).
/// </summary>
public record ErrorMessage : ControlEnvelope
{
    public ErrorMessage() => T = FrameTypes.Error;

    /// <summary>A typed error code, e.g. <c>unsupported</c>, <c>not_ready</c>, <c>internal</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>Human-readable detail.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
