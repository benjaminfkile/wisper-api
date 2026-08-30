using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Owner-side pumps that bridge a <b>local</b> shell/exec stream (the one physically wired to the host
/// socket on this instance) to a <see cref="StreamFrame"/> conversation on the backplane, so a consumer
/// stream living on another instance can drive it (docs/DESIGN.md §7). The caller-side halves are
/// <see cref="RemoteTunnelShell"/>/<see cref="RemoteTunnelExec"/>. Real per-stream credit flow control
/// (docs/TUNNEL.md §9) is preserved end-to-end: the owner only replenishes the host-facing window when
/// the caller acks bytes it has drained, forwarded here as <c>credit</c> frames.
/// </summary>
internal static class BackplaneStreamBridge
{
    /// <summary>
    /// Bridges a local <see cref="ITunnelExec"/>: forwards its channel-tagged output downstream and
    /// applies inbound <c>credit</c>/<c>close</c> from the caller. Runs until the exec ends, then emits a
    /// terminal <c>exit</c> (or <c>close</c>) frame and unsubscribes. Fire-and-forget.
    /// </summary>
    public static async Task RunExecBridgeAsync(
        ITunnelBackplane backplane, string prefix, string correlationId, ITunnelExec exec, ILogger logger)
    {
        var down = BackplaneChannels.StreamDown(prefix, correlationId);
        var up = BackplaneChannels.StreamUp(prefix, correlationId);

        var upSubscription = await backplane.SubscribeAsync(up, async (payload, ct) =>
        {
            var frame = BackplaneJson.Deserialize<StreamFrame>(payload);
            if (frame is null)
            {
                return;
            }

            switch (frame.Kind)
            {
                case "credit":
                    await exec.AckDrainedAsync(frame.Bytes, ct);
                    break;
                case "close":
                    await exec.CloseAsync(frame.Reason ?? "consumer_closed", ct);
                    break;
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in exec.Output.ReadAllAsync())
                {
                    var frame = new StreamFrame
                    {
                        Kind = "data",
                        Channel = chunk.Channel,
                        Data = Convert.ToBase64String(chunk.Data),
                    };
                    await backplane.PublishAsync(down, BackplaneJson.Serialize(frame));
                }

                var terminal = exec.ExitCode is int code
                    ? new StreamFrame { Kind = "exit", ExitCode = code }
                    : new StreamFrame { Kind = "close", Reason = exec.ClosedReason ?? "closed" };
                await backplane.PublishAsync(down, BackplaneJson.Serialize(terminal));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "backplane exec bridge {CorrelationId} faulted", correlationId);
            }
            finally
            {
                await upSubscription.DisposeAsync();
            }
        });
    }

    /// <summary>
    /// Bridges a local <see cref="ITunnelFileDownload"/>: forwards each drained byte chunk downstream
    /// and applies inbound <c>credit</c>/<c>close</c> from the caller. Runs until the download ends,
    /// then emits a terminal <c>close</c> (carrying the reason, e.g. <c>file_eof</c>) and unsubscribes.
    /// </summary>
    public static async Task RunFileDownloadBridgeAsync(
        ITunnelBackplane backplane, string prefix, string correlationId,
        ITunnelFileDownload download, ILogger logger)
    {
        var down = BackplaneChannels.StreamDown(prefix, correlationId);
        var up = BackplaneChannels.StreamUp(prefix, correlationId);

        var upSubscription = await backplane.SubscribeAsync(up, async (payload, ct) =>
        {
            var frame = BackplaneJson.Deserialize<StreamFrame>(payload);
            if (frame is null)
            {
                return;
            }

            switch (frame.Kind)
            {
                case "credit":
                    await download.AckDrainedAsync(frame.Bytes, ct);
                    break;
                case "close":
                    await download.CloseAsync(frame.Reason ?? "consumer_closed", ct);
                    break;
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in download.Bytes.ReadAllAsync())
                {
                    var frame = new StreamFrame
                    {
                        Kind = "data",
                        Channel = Channels.Stdout,
                        Data = Convert.ToBase64String(chunk),
                    };
                    await backplane.PublishAsync(down, BackplaneJson.Serialize(frame));
                }

                var terminal = new StreamFrame { Kind = "close", Reason = download.ClosedReason ?? "closed" };
                await backplane.PublishAsync(down, BackplaneJson.Serialize(terminal));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "backplane file bridge {CorrelationId} faulted", correlationId);
            }
            finally
            {
                await upSubscription.DisposeAsync();
            }
        });
    }

    /// <summary>
    public static async Task RunShellBridgeAsync(
        ITunnelBackplane backplane, string prefix, string correlationId, ITunnelShell shell, ILogger logger)
    {
        var down = BackplaneChannels.StreamDown(prefix, correlationId);
        var up = BackplaneChannels.StreamUp(prefix, correlationId);

        var upSubscription = await backplane.SubscribeAsync(up, async (payload, ct) =>
        {
            var frame = BackplaneJson.Deserialize<StreamFrame>(payload);
            if (frame is null)
            {
                return;
            }

            switch (frame.Kind)
            {
                case "stdin":
                    if (frame.Data is not null)
                    {
                        await shell.WriteStdinAsync(Convert.FromBase64String(frame.Data), ct);
                    }

                    break;
                case "resize":
                    await shell.ResizeAsync(frame.Cols, frame.Rows, ct);
                    break;
                case "credit":
                    await shell.AckOutputDrainedAsync(frame.Bytes, ct);
                    break;
                case "close":
                    await shell.CloseAsync(frame.Reason ?? "consumer_closed", ct);
                    break;
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var bytes in shell.Output.ReadAllAsync())
                {
                    var frame = new StreamFrame
                    {
                        Kind = "data",
                        Channel = Channels.Stdout,
                        Data = Convert.ToBase64String(bytes),
                    };
                    await backplane.PublishAsync(down, BackplaneJson.Serialize(frame));
                }

                var terminal = new StreamFrame { Kind = "close", Reason = shell.ClosedReason ?? "closed" };
                await backplane.PublishAsync(down, BackplaneJson.Serialize(terminal));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "backplane shell bridge {CorrelationId} faulted", correlationId);
            }
            finally
            {
                await upSubscription.DisposeAsync();
            }
        });
    }
}

/// <summary>
/// Caller-side <see cref="ITunnelExec"/> over the backplane: the consumer instance's handle to a
/// streamed exec whose real socket lives on another instance. Output frames arriving on the down
/// channel are re-emitted as <see cref="ExecChunk"/>s; drain acks and close go up the backplane to the
/// owning instance's <see cref="BackplaneStreamBridge.RunExecBridgeAsync"/>.
/// </summary>
internal sealed class RemoteTunnelExec : ITunnelExec
{
    private readonly ITunnelBackplane _backplane;
    private readonly string _upChannel;
    private readonly Channel<ExecChunk> _chunks =
        Channel.CreateUnbounded<ExecChunk>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IAsyncDisposable? _subscription;
    private int _exitCode;
    private volatile bool _hasExit;
    private volatile string? _closedReason;
    private int _closed;

    public RemoteTunnelExec(ITunnelBackplane backplane, string upChannel, uint sid)
    {
        _backplane = backplane;
        _upChannel = upChannel;
        Sid = sid;
    }

    public uint Sid { get; private set; }

    /// <summary>Adopts the owner-allocated stream id (returned in the open reply) and returns this handle.</summary>
    public RemoteTunnelExec WithSid(uint sid)
    {
        Sid = sid;
        return this;
    }

    public ChannelReader<ExecChunk> Output => _chunks.Reader;

    public Task Completion => _completion.Task;

    public int? ExitCode => _hasExit ? _exitCode : null;

    public string? ClosedReason => _closedReason;

    /// <summary>Attaches the down-channel subscription so it is torn down when the stream closes.</summary>
    public void AttachSubscription(IAsyncDisposable subscription) => _subscription = subscription;

    /// <summary>Applies one owner→caller frame (invoked serially by the backplane subscription pump).</summary>
    public Task HandleDownAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var frame = BackplaneJson.Deserialize<StreamFrame>(payload);
        if (frame is null)
        {
            return Task.CompletedTask;
        }

        switch (frame.Kind)
        {
            case "data" when frame.Data is not null:
                _chunks.Writer.TryWrite(new ExecChunk(frame.Channel, Convert.FromBase64String(frame.Data)));
                break;
            case "exit":
                _exitCode = frame.ExitCode;
                _hasExit = true;
                Finish(null);
                break;
            case "close":
                Finish(frame.Reason ?? "closed");
                break;
        }

        return Task.CompletedTask;
    }

    public async ValueTask AckDrainedAsync(int byteCount, CancellationToken ct = default)
    {
        if (byteCount <= 0 || _closed != 0)
        {
            return;
        }

        await _backplane.PublishAsync(
            _upChannel, BackplaneJson.Serialize(new StreamFrame { Kind = "credit", Bytes = byteCount }), ct);
    }

    public async Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            await _backplane.PublishAsync(
                _upChannel, BackplaneJson.Serialize(new StreamFrame { Kind = "close", Reason = reason }), ct);
        }
        catch
        {
            // Best effort -- the owning instance may already be gone.
        }

        Finish(reason);

        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private void Finish(string? reason)
    {
        _closedReason ??= reason;
        _chunks.Writer.TryComplete();
        _completion.TrySetResult();
    }
}

/// <summary>
/// Caller-side <see cref="ITunnelFileDownload"/> over the backplane: the consumer instance's handle to
/// a file-download stream whose real socket lives on another instance. Byte frames arrive on the down
/// channel; drain acks and close go up to the owning instance's
/// <see cref="BackplaneStreamBridge.RunFileDownloadBridgeAsync"/>.
/// </summary>
internal sealed class RemoteTunnelFileDownload : ITunnelFileDownload
{
    private readonly ITunnelBackplane _backplane;
    private readonly string _upChannel;
    private readonly Channel<byte[]> _bytes =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IAsyncDisposable? _subscription;
    private volatile string? _closedReason;
    private int _closed;

    public RemoteTunnelFileDownload(ITunnelBackplane backplane, string upChannel, uint sid, long size)
    {
        _backplane = backplane;
        _upChannel = upChannel;
        Sid = sid;
        Size = size;
    }

    public uint Sid { get; private set; }

    public long Size { get; private set; }

    /// <summary>Adopts the owner-allocated sid + reported size (returned in the open reply) and returns this handle.</summary>
    public RemoteTunnelFileDownload WithOpened(uint sid, long size)
    {
        Sid = sid;
        Size = size;
        return this;
    }

    public ChannelReader<byte[]> Bytes => _bytes.Reader;

    public Task Completion => _completion.Task;

    public string? ClosedReason => _closedReason;

    public void AttachSubscription(IAsyncDisposable subscription) => _subscription = subscription;

    public Task HandleDownAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var frame = BackplaneJson.Deserialize<StreamFrame>(payload);
        if (frame is null)
        {
            return Task.CompletedTask;
        }

        switch (frame.Kind)
        {
            case "data" when frame.Data is not null:
                _bytes.Writer.TryWrite(Convert.FromBase64String(frame.Data));
                break;
            case "close":
                Finish(frame.Reason ?? "closed");
                break;
        }

        return Task.CompletedTask;
    }

    public async ValueTask AckDrainedAsync(int byteCount, CancellationToken ct = default)
    {
        if (byteCount <= 0 || _closed != 0)
        {
            return;
        }

        await _backplane.PublishAsync(
            _upChannel, BackplaneJson.Serialize(new StreamFrame { Kind = "credit", Bytes = byteCount }), ct);
    }

    public async Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            await _backplane.PublishAsync(
                _upChannel, BackplaneJson.Serialize(new StreamFrame { Kind = "close", Reason = reason }), ct);
        }
        catch
        {
            // Best effort -- the owning instance may already be gone.
        }

        Finish(reason);

        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private void Finish(string? reason)
    {
        _closedReason ??= reason;
        _bytes.Writer.TryComplete();
        _completion.TrySetResult();
    }
}

/// <summary>
/// Caller-side <see cref="ITunnelShell"/> over the backplane: the consumer instance's handle to an
/// interactive shell whose real socket lives on another instance. PTY output arrives on the down
/// channel; keystrokes, resize, drain-credit and close go up to the owning instance's
/// <see cref="BackplaneStreamBridge.RunShellBridgeAsync"/>.
/// </summary>
internal sealed class RemoteTunnelShell : ITunnelShell
{
    private readonly ITunnelBackplane _backplane;
    private readonly string _upChannel;
    private readonly Channel<byte[]> _output =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IAsyncDisposable? _subscription;
    private volatile string? _closedReason;
    private int _closed;

    public RemoteTunnelShell(ITunnelBackplane backplane, string upChannel, uint sid)
    {
        _backplane = backplane;
        _upChannel = upChannel;
        Sid = sid;
    }

    public uint Sid { get; private set; }

    /// <summary>Adopts the owner-allocated stream id (returned in the open reply) and returns this handle.</summary>
    public RemoteTunnelShell WithSid(uint sid)
    {
        Sid = sid;
        return this;
    }

    public ChannelReader<byte[]> Output => _output.Reader;

    public Task Completion => _completion.Task;

    public string? ClosedReason => _closedReason;

    public void AttachSubscription(IAsyncDisposable subscription) => _subscription = subscription;

    public Task HandleDownAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var frame = BackplaneJson.Deserialize<StreamFrame>(payload);
        if (frame is null)
        {
            return Task.CompletedTask;
        }

        switch (frame.Kind)
        {
            case "data" when frame.Data is not null:
                _output.Writer.TryWrite(Convert.FromBase64String(frame.Data));
                break;
            case "close":
                Finish(frame.Reason ?? "closed");
                break;
        }

        return Task.CompletedTask;
    }

    public Task WriteStdinAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        _backplane.PublishAsync(
            _upChannel,
            BackplaneJson.Serialize(new StreamFrame { Kind = "stdin", Data = Convert.ToBase64String(data.Span) }),
            ct);

    public ValueTask AckOutputDrainedAsync(int byteCount, CancellationToken ct = default)
    {
        if (byteCount <= 0 || _closed != 0)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_backplane.PublishAsync(
            _upChannel, BackplaneJson.Serialize(new StreamFrame { Kind = "credit", Bytes = byteCount }), ct));
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct = default) =>
        _backplane.PublishAsync(
            _upChannel,
            BackplaneJson.Serialize(new StreamFrame { Kind = "resize", Cols = cols, Rows = rows }),
            ct);

    public async Task CloseAsync(string reason = "consumer_closed", CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            await _backplane.PublishAsync(
                _upChannel, BackplaneJson.Serialize(new StreamFrame { Kind = "close", Reason = reason }), ct);
        }
        catch
        {
            // Best effort -- the owning instance may already be gone.
        }

        Finish(reason);

        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private void Finish(string? reason)
    {
        _closedReason ??= reason;
        _output.Writer.TryComplete();
        _completion.TrySetResult();
    }
}
