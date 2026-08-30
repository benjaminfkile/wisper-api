using System.Text.Json;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>The relay operation a routed RPC request carries out on the owning instance.</summary>
internal enum RelayOp
{
    CreateLease,
    Exec,
    Release,
    OpenShell,
    OpenExecStream,
    OpenFileRead,
}

/// <summary>
/// A relay request routed from the instance handling a consumer call to the instance that owns the
/// host's tunnel (docs/DESIGN.md §7 -- "publishes the frame to A's channel with a correlation id"). The
/// owner executes it against its <b>local</b> relay (which owns the socket + its id space) and replies.
/// </summary>
internal sealed record RpcRequest
{
    /// <summary>Correlates this request with its <see cref="RpcReply"/>.</summary>
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>The instance to publish the reply back to.</summary>
    public string ReplyToInstance { get; init; } = string.Empty;

    /// <summary>Which relay operation to run (see <see cref="RelayOp"/>).</summary>
    public string Op { get; init; } = string.Empty;

    public string HostId { get; init; } = string.Empty;
    public string? LeaseId { get; init; }
    public string? Command { get; init; }

    /// <summary>Absolute file path for <see cref="RelayOp.OpenFileRead"/>.</summary>
    public string? Path { get; init; }

    /// <summary>The lease spec for <see cref="RelayOp.CreateLease"/> (its rid/leaseId are re-stamped by the owner).</summary>
    public LeaseCreate? Spec { get; init; }

    public int Cols { get; init; }
    public int Rows { get; init; }
}

/// <summary>The owning instance's reply to an <see cref="RpcRequest"/>, routed back to the caller.</summary>
internal sealed record RpcReply
{
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary><c>true</c> if the operation succeeded; otherwise <see cref="ErrorCode"/>/<see cref="ErrorMessage"/> are set.</summary>
    public bool Ok { get; init; }

    /// <summary>The <c>ApiErrorCode</c> name when the op failed (mapped back to a typed <c>ApiException</c> at the caller).</summary>
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Result of a successful <see cref="RelayOp.CreateLease"/>.</summary>
    public LeaseResult? Lease { get; init; }

    /// <summary>Result of a successful <see cref="RelayOp.Exec"/>.</summary>
    public ExecResult? Exec { get; init; }

    /// <summary>The owner-allocated stream id of a successfully opened shell/exec stream.</summary>
    public uint Sid { get; init; }

    /// <summary>Total file size (bytes) of a successfully opened file-read stream, or <c>-1</c> when unknown.</summary>
    public long Size { get; init; }
}

/// <summary>
/// One message on a bridged byte-stream channel (docs/DESIGN.md §7 -- a consumer stream on one instance
/// bridged to a host tunnel on another). Down = owner→caller (agent output / exit / close); Up =
/// caller→owner (stdin, credit, resize, close). Raw bytes ride as base64 to stay JSON-debuggable like
/// the control frames; the high-volume socket hop is on the owning instance, not here.
/// </summary>
internal sealed record StreamFrame
{
    /// <summary>One of: <c>data</c>, <c>stdin</c>, <c>credit</c>, <c>resize</c>, <c>close</c>, <c>exit</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Wire channel for <c>data</c> (1=stdout, 2=stderr).</summary>
    public byte Channel { get; init; }

    /// <summary>Base64 payload for <c>data</c>/<c>stdin</c>.</summary>
    public string? Data { get; init; }

    /// <summary>Byte count for <c>credit</c> (flow-control replenishment).</summary>
    public int Bytes { get; init; }

    public int Cols { get; init; }
    public int Rows { get; init; }

    /// <summary>Close/end reason for <c>close</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>Process exit code for <c>exit</c>.</summary>
    public int ExitCode { get; init; }
}

/// <summary>Channel-name conventions for the backplane (all under the configured prefix).</summary>
internal static class BackplaneChannels
{
    public static string Request(string prefix, string instanceId) => $"{prefix}:rpc:req:{instanceId}";

    public static string Reply(string prefix, string instanceId) => $"{prefix}:rpc:rep:{instanceId}";

    public static string StreamDown(string prefix, string correlationId) => $"{prefix}:stream:{correlationId}:down";

    public static string StreamUp(string prefix, string correlationId) => $"{prefix}:stream:{correlationId}:up";
}

/// <summary>Shared JSON (de)serialization for backplane messages -- reuses the tunnel control-frame options.</summary>
internal static class BackplaneJson
{
    public static byte[] Serialize<T>(T message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, ControlJson.Options);

    public static T? Deserialize<T>(ReadOnlyMemory<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload.Span, ControlJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
