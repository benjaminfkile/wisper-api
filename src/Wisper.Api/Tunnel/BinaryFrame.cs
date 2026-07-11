using System.Buffers.Binary;

namespace Wisper.Api.Tunnel;

/// <summary>
/// A binary tunnel data frame (docs/TUNNEL.md §2): a 6-byte big-endian header
/// followed by a raw payload. Carried as a WebSocket <b>binary</b> frame.
/// <code>
///  byte 0      uint8    ver     = 0x01
///  byte 1      uint8    ch      channel (0=stdin, 1=stdout, 2=stderr)
///  bytes 2..5  uint32   sid     stream id, BIG-ENDIAN
///  bytes 6..N  bytes    data    raw payload, length &lt;= 32 KiB
/// </code>
/// The layout is byte-for-byte compatible with the Go agent.
/// </summary>
public readonly record struct BinaryFrame(byte Channel, uint Sid, ReadOnlyMemory<byte> Data)
{
    /// <summary>Fixed header length in bytes.</summary>
    public const int HeaderSize = 6;

    /// <summary>Maximum payload length per frame — 32 KiB (docs/TUNNEL.md §2).</summary>
    public const int MaxPayload = 32 * 1024;

    /// <summary>
    /// Serializes this frame to a fresh byte array (header + payload).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Payload exceeds <see cref="MaxPayload"/>.</exception>
    public byte[] Encode()
    {
        var payload = Data.Span;
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Data),
                payload.Length,
                $"Binary frame payload exceeds MaxPayload ({MaxPayload} bytes).");
        }

        var buffer = new byte[HeaderSize + payload.Length];
        buffer[0] = TunnelProtocol.ProtocolVersion;
        buffer[1] = Channel;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(2, 4), Sid);
        payload.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }

    /// <summary>
    /// Attempts to decode a binary frame from <paramref name="source"/>. Returns
    /// <c>false</c> (without throwing) for a short frame (&lt; 6 bytes), a bad
    /// version byte (!= 0x01), or an oversize payload (&gt; <see cref="MaxPayload"/>).
    /// The decoded frame's <see cref="Data"/> is a copy independent of the input.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> source, out BinaryFrame frame)
    {
        frame = default;

        if (source.Length < HeaderSize)
        {
            return false;
        }

        if (source[0] != TunnelProtocol.ProtocolVersion)
        {
            return false;
        }

        var payloadLength = source.Length - HeaderSize;
        if (payloadLength > MaxPayload)
        {
            return false;
        }

        var channel = source[1];
        var sid = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(2, 4));
        var payload = source.Slice(HeaderSize).ToArray();
        frame = new BinaryFrame(channel, sid, payload);
        return true;
    }

    /// <summary>
    /// Decodes a binary frame, throwing <see cref="BinaryFrameException"/> on any
    /// framing violation (short frame, bad version, oversize payload).
    /// </summary>
    /// <exception cref="BinaryFrameException">The buffer is not a valid frame.</exception>
    public static BinaryFrame Decode(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize)
        {
            throw new BinaryFrameException(
                $"Short binary frame: got {source.Length} bytes, need at least {HeaderSize}.");
        }

        if (source[0] != TunnelProtocol.ProtocolVersion)
        {
            throw new BinaryFrameException(
                $"Bad binary frame version: got 0x{source[0]:X2}, expected 0x{TunnelProtocol.ProtocolVersion:X2}.");
        }

        var payloadLength = source.Length - HeaderSize;
        if (payloadLength > MaxPayload)
        {
            throw new BinaryFrameException(
                $"Oversize binary frame payload: {payloadLength} bytes exceeds MaxPayload ({MaxPayload}).");
        }

        var channel = source[1];
        var sid = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(2, 4));
        var payload = source.Slice(HeaderSize).ToArray();
        return new BinaryFrame(channel, sid, payload);
    }
}

/// <summary>Thrown by <see cref="BinaryFrame.Decode"/> when a buffer is not a valid frame.</summary>
public sealed class BinaryFrameException : Exception
{
    public BinaryFrameException(string message) : base(message)
    {
    }
}
