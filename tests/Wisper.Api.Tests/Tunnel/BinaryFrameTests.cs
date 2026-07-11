using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Wire-format tests for <see cref="BinaryFrame"/> — the 6-byte big-endian header
/// + payload layout must stay byte-for-byte compatible with the Go agent
/// (docs/TUNNEL.md §2).
/// </summary>
public class BinaryFrameTests
{
    [Fact]
    public void RoundTrip_preserves_channel_sid_and_payload()
    {
        // sid > 0x7fffffff proves the field is unsigned and big-endian.
        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        var original = new BinaryFrame(Channels.Stdout, 0xDEADBEEF, payload);

        var encoded = original.Encode();
        var ok = BinaryFrame.TryDecode(encoded, out var decoded);

        Assert.True(ok);
        Assert.Equal(Channels.Stdout, decoded.Channel);
        Assert.Equal(0xDEADBEEFu, decoded.Sid);
        Assert.Equal(payload, decoded.Data.ToArray());
    }

    [Fact]
    public void Encode_produces_exact_header_bytes()
    {
        var frame = new BinaryFrame(Channel: 1, Sid: 0xDEADBEEF, Data: ReadOnlyMemory<byte>.Empty);

        var encoded = frame.Encode();

        // ver=01, ch=01, sid=DE AD BE EF (big-endian).
        Assert.Equal(new byte[] { 0x01, 0x01, 0xDE, 0xAD, 0xBE, 0xEF }, encoded);
    }

    [Fact]
    public void Encode_at_MaxPayload_succeeds()
    {
        var payload = new byte[BinaryFrame.MaxPayload];
        var frame = new BinaryFrame(Channels.Stdout, 1, payload);

        var encoded = frame.Encode();

        Assert.Equal(BinaryFrame.HeaderSize + BinaryFrame.MaxPayload, encoded.Length);
    }

    [Fact]
    public void Encode_over_MaxPayload_throws()
    {
        var payload = new byte[BinaryFrame.MaxPayload + 1];
        var frame = new BinaryFrame(Channels.Stdout, 1, payload);

        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Encode());
    }

    [Fact]
    public void TryDecode_short_frame_fails()
    {
        var tooShort = new byte[] { 0x01, 0x01, 0xDE, 0xAD, 0xBE }; // 5 bytes < header.

        var ok = BinaryFrame.TryDecode(tooShort, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryDecode_bad_version_fails()
    {
        var badVersion = new byte[] { 0x02, 0x01, 0x00, 0x00, 0x00, 0x01 };

        var ok = BinaryFrame.TryDecode(badVersion, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryDecode_oversize_payload_fails()
    {
        var oversize = new byte[BinaryFrame.HeaderSize + BinaryFrame.MaxPayload + 1];
        oversize[0] = 0x01;

        var ok = BinaryFrame.TryDecode(oversize, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Decode_short_frame_throws()
    {
        var tooShort = new byte[] { 0x01, 0x01, 0xDE };

        Assert.Throws<BinaryFrameException>(() => BinaryFrame.Decode(tooShort));
    }

    [Fact]
    public void Decode_bad_version_throws()
    {
        var badVersion = new byte[] { 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00 };

        Assert.Throws<BinaryFrameException>(() => BinaryFrame.Decode(badVersion));
    }

    [Fact]
    public void Decode_valid_frame_round_trips()
    {
        var original = new BinaryFrame(Channels.Stderr, 42, new byte[] { 0xAB, 0xCD });

        var decoded = BinaryFrame.Decode(original.Encode());

        Assert.Equal(Channels.Stderr, decoded.Channel);
        Assert.Equal(42u, decoded.Sid);
        Assert.Equal(new byte[] { 0xAB, 0xCD }, decoded.Data.ToArray());
    }

    [Fact]
    public void Decode_empty_payload_frame_succeeds()
    {
        var header = new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x07 };

        var decoded = BinaryFrame.Decode(header);

        Assert.Equal(Channels.Stdin, decoded.Channel);
        Assert.Equal(7u, decoded.Sid);
        Assert.Equal(0, decoded.Data.Length);
    }
}
