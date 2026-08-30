using System.Threading.Channels;
using Wisper.Api.Tunnel;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// An <see cref="ITunnelFileDownload"/> double for the file-download HTTP relay tests: replays a preset
/// sequence of byte chunks and reports a preset total <c>Size</c>. Records the drained byte-count the
/// relay acks back and whether it was closed, so a test can assert credit accounting and teardown.
/// </summary>
public sealed class FakeTunnelFileDownload : ITunnelFileDownload
{
    private readonly Channel<byte[]> _bytes =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    public FakeTunnelFileDownload(IEnumerable<byte[]> chunks, long size, string? closedReason = null)
    {
        foreach (var chunk in chunks)
        {
            _bytes.Writer.TryWrite(chunk);
        }

        _bytes.Writer.TryComplete();
        Size = size;
        ClosedReason = closedReason;
    }

    public uint Sid => 1;

    public long Size { get; }

    public ChannelReader<byte[]> Bytes => _bytes.Reader;

    public Task Completion => Task.CompletedTask;

    public string? ClosedReason { get; }

    /// <summary>Total bytes the relay acked as drained across <see cref="AckDrainedAsync"/> calls.</summary>
    public int DrainedBytes { get; private set; }

    /// <summary>Whether <see cref="CloseAsync"/> ran (the relay tore the stream down).</summary>
    public bool Closed { get; private set; }

    public ValueTask AckDrainedAsync(int byteCount, CancellationToken ct = default)
    {
        DrainedBytes += byteCount;
        return ValueTask.CompletedTask;
    }

    public Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default)
    {
        Closed = true;
        _bytes.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
