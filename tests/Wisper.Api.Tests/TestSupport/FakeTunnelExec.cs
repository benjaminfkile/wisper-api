using System.Threading.Channels;
using Wisper.Api.Tunnel;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// An <see cref="ITunnelExec"/> double for the streamed-exec SSE tests (Grunt has no live agent tunnel).
/// It replays a preset sequence of channel-tagged output chunks, then completes with a preset exit code
/// (or a <see cref="ClosedReason"/> for the abnormal-end path). It records the drained byte-count the
/// relay acks back and whether it was closed, so a test can assert the relay drained and tore down the
/// stream. All chunks are enqueued up front, so <see cref="Output"/> is already complete on construction.
/// </summary>
public sealed class FakeTunnelExec : ITunnelExec
{
    private readonly Channel<ExecChunk> _chunks =
        Channel.CreateUnbounded<ExecChunk>(new UnboundedChannelOptions { SingleReader = true });

    public FakeTunnelExec(IEnumerable<ExecChunk> chunks, int? exitCode, string? closedReason = null)
    {
        foreach (var chunk in chunks)
        {
            _chunks.Writer.TryWrite(chunk);
        }

        _chunks.Writer.TryComplete();
        ExitCode = exitCode;
        ClosedReason = closedReason;
    }

    public uint Sid => 1;

    public ChannelReader<ExecChunk> Output => _chunks.Reader;

    public int? ExitCode { get; }

    public string? ClosedReason { get; }

    /// <summary>Total bytes the relay acked as drained across <see cref="AckDrainedAsync"/> calls.</summary>
    public int DrainedBytes { get; private set; }

    /// <summary>Whether <see cref="CloseAsync"/> ran (the relay tore the stream down).</summary>
    public bool Closed { get; private set; }

    public Task Completion => Task.CompletedTask;

    public ValueTask AckDrainedAsync(int byteCount, CancellationToken ct = default)
    {
        DrainedBytes += byteCount;
        return ValueTask.CompletedTask;
    }

    public Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default)
    {
        Closed = true;
        _chunks.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}
