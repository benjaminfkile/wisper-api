using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wisper.Api.Tunnel;

/// <summary>
/// The common header of every JSON control frame (docs/TUNNEL.md §5):
/// <c>{ "t": "&lt;type&gt;", "rid": &lt;uint32&gt;, "sid": &lt;uint32&gt;, ... }</c>.
/// <para>
/// <see cref="Rid"/> and <see cref="Sid"/> are omitted from the serialized JSON
/// when their value is <c>0</c>, matching the Go agent's <c>omitempty</c> so the
/// bytes on the wire are equivalent in both directions. Concrete per-type fields
/// are layered on by later tasks; this type carries the envelope only.
/// </para>
/// </summary>
public record ControlEnvelope
{
    /// <summary>The control frame type (one of <see cref="FrameTypes"/>). Always present.</summary>
    [JsonPropertyName("t")]
    public string T { get; init; } = string.Empty;

    /// <summary>Request id correlating a request with its response. Omitted when 0.</summary>
    [JsonPropertyName("rid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Rid { get; init; }

    /// <summary>Stream id for a long-lived byte stream. Omitted when 0.</summary>
    [JsonPropertyName("sid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Sid { get; init; }
}

/// <summary>Shared JSON (de)serialization for tunnel control frames.</summary>
public static class ControlJson
{
    /// <summary>
    /// The serializer options every control frame uses. <c>omitempty</c>-equivalent
    /// behaviour comes from per-property <c>[JsonIgnore(WhenWritingDefault)]</c>, but
    /// the global default condition is set too so later concrete types inherit it.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        PropertyNamingPolicy = null,
    };

    /// <summary>Serializes a control frame to its UTF-8 JSON text form.</summary>
    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Deserializes a control frame from its JSON text form.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// Reads only the <c>t</c> (frame type) from an inbound control message without
    /// materializing the concrete type, so a dispatcher can route on it. Returns
    /// <c>null</c> when the JSON is malformed or has no string <c>t</c> property.
    /// </summary>
    public static string? PeekType(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return null;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var isType = reader.ValueTextEquals("t");
                if (!reader.Read())
                {
                    return null;
                }

                if (isType)
                {
                    return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }

                // Skip the value (including nested objects/arrays) of a non-`t` property.
                reader.Skip();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads only the <c>t</c> (frame type) from an inbound control message string.
    /// See <see cref="PeekType(ReadOnlySpan{byte})"/>.
    /// </summary>
    public static string? PeekType(string json) =>
        PeekType(System.Text.Encoding.UTF8.GetBytes(json));
}
