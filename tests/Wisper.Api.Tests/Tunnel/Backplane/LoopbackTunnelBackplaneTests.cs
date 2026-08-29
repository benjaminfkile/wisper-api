using System.Text;
using Wisper.Api.Tunnel.Backplane;
using Xunit;

namespace Wisper.Api.Tests.Tunnel.Backplane;

/// <summary>
/// Unit tests for the in-process <see cref="LoopbackTunnelBackplane"/> -- the "fake/looped backplane" the
/// design calls for (docs/DESIGN.md §7). It stands in for Redis pub/sub in the single-process suite, so it
/// must fan a publish out to every live subscriber on the channel, deliver in publish order, isolate
/// channels, and stop delivering once a subscription is disposed.
/// </summary>
public class LoopbackTunnelBackplaneTests
{
    private static byte[] Msg(string s) => Encoding.UTF8.GetBytes(s);

    private static string Str(ReadOnlyMemory<byte> b) => Encoding.UTF8.GetString(b.Span);

    [Fact]
    public async Task Publish_reaches_every_subscriber_on_the_channel()
    {
        var backplane = new LoopbackTunnelBackplane();
        var a = new List<string>();
        var b = new List<string>();
        var gate = new SemaphoreSlim(0);

        await using var subA = await backplane.SubscribeAsync("chan", (m, _) =>
        {
            a.Add(Str(m));
            gate.Release();
            return Task.CompletedTask;
        });
        await using var subB = await backplane.SubscribeAsync("chan", (m, _) =>
        {
            b.Add(Str(m));
            gate.Release();
            return Task.CompletedTask;
        });

        await backplane.PublishAsync("chan", Msg("hello"));

        await gate.WaitAsync(TimeSpan.FromSeconds(5));
        await gate.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "hello" }, a);
        Assert.Equal(new[] { "hello" }, b);
    }

    [Fact]
    public async Task Messages_are_delivered_in_publish_order()
    {
        var backplane = new LoopbackTunnelBackplane();
        var received = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var sub = await backplane.SubscribeAsync("chan", (m, _) =>
        {
            var s = Str(m);
            received.Add(s);
            if (s == "3")
            {
                done.TrySetResult();
            }

            return Task.CompletedTask;
        });

        await backplane.PublishAsync("chan", Msg("1"));
        await backplane.PublishAsync("chan", Msg("2"));
        await backplane.PublishAsync("chan", Msg("3"));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "1", "2", "3" }, received);
    }

    [Fact]
    public async Task Subscribers_only_see_their_own_channel()
    {
        var backplane = new LoopbackTunnelBackplane();
        var got = new List<string>();
        var gate = new SemaphoreSlim(0);

        await using var sub = await backplane.SubscribeAsync("wanted", (m, _) =>
        {
            got.Add(Str(m));
            gate.Release();
            return Task.CompletedTask;
        });

        await backplane.PublishAsync("other", Msg("nope"));
        await backplane.PublishAsync("wanted", Msg("yes"));

        await gate.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "yes" }, got);
    }

    [Fact]
    public async Task Disposed_subscription_stops_receiving()
    {
        var backplane = new LoopbackTunnelBackplane();
        var count = 0;
        var gate = new SemaphoreSlim(0);

        var sub = await backplane.SubscribeAsync("chan", (_, _) =>
        {
            Interlocked.Increment(ref count);
            gate.Release();
            return Task.CompletedTask;
        });

        await backplane.PublishAsync("chan", Msg("one"));
        await gate.WaitAsync(TimeSpan.FromSeconds(5));

        await sub.DisposeAsync();
        await backplane.PublishAsync("chan", Msg("two"));

        // No live subscriber remains, so the second publish is dropped -- the count stays at 1.
        Assert.False(await gate.WaitAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Publish_to_a_channel_with_no_subscribers_is_a_noop()
    {
        var backplane = new LoopbackTunnelBackplane();
        await backplane.PublishAsync("nobody", Msg("x"));
    }
}
