using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wisper.Api.Infrastructure;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel.Backplane;

/// <summary>
/// The core P8.1 behaviour, driven entirely over the in-process <see cref="LoopbackTunnelBackplane"/> --
/// no Redis (docs/DESIGN.md §7, docs/TUNNEL.md §11). Two simulated manager instances (A and B) share one
/// loopback + one presence store. A host's tunnel is "pinned" to instance A (A owns the socket); a
/// consumer call landing on instance B must be routed to A over the backplane with a correlation id, run
/// against A's local relay, and its reply routed back to B. Byte streams (exec/shell) are bridged the
/// same way, preserving flow-control credit end-to-end.
/// </summary>
public class DistributedTunnelRelayTests
{
    private readonly LoopbackTunnelBackplane _backplane = new();
    private readonly InMemoryHostPresenceStore _presence = new();
    private readonly IOptions<BackplaneOptions> _options =
        Options.Create(new BackplaneOptions { ChannelPrefix = "wisper", RpcTimeoutMs = 5000 });

    private static LeaseCreate Spec() => new()
    {
        Image = "alpine",
        Network = "none",
        Resources = new LeaseResources { Cpus = 2, MemoryMb = 1024, Pids = 256 },
        TtlSeconds = 3600,
    };

    private async Task<Instance> StartInstanceAsync(string id)
    {
        var local = new OwnerTunnelRelayStub();
        var registry = new LocalHostRegistryStub();
        var relay = new DistributedTunnelRelay(
            local, registry, _presence, _backplane, new WisperInstanceIdentity(id), _options,
            NullLogger<DistributedTunnelRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);
        return new Instance(relay, local, registry);
    }

    private sealed record Instance(DistributedTunnelRelay Relay, OwnerTunnelRelayStub Local, LocalHostRegistryStub Registry);

    // ----- routed RPC -----------------------------------------------------------------------------

    [Fact]
    public async Task Create_lease_on_a_remote_instance_is_routed_to_the_owner()
    {
        var a = await StartInstanceAsync("A");
        var b = await StartInstanceAsync("B");
        a.Registry.MarkLocal("host-x");
        await _presence.SetOwnerAsync("host-x", "A");
        a.Local.LeaseResult = new LeaseResult("lease_1", "wc_1", "provisioning");

        var result = await b.Relay.CreateLeaseAsync("host-x", Spec());

        Assert.Equal("lease_1", result.LeaseId);
        Assert.Equal("wc_1", result.WispContractId);
        // Ran on the owner (A), not the caller (B).
        Assert.Single(a.Local.CreateCalls);
        Assert.Equal("host-x", a.Local.CreateCalls[0].HostId);
        Assert.Empty(b.Local.CreateCalls);
    }

    [Fact]
    public async Task Exec_on_a_remote_instance_is_routed_to_the_owner()
    {
        var a = await StartInstanceAsync("A");
        var b = await StartInstanceAsync("B");
        await _presence.SetOwnerAsync("host-x", "A");
        a.Local.ExecResult = new ExecResult { Stdout = "hi\n", Stderr = string.Empty, ExitCode = 0 };

        var result = await b.Relay.ExecAsync("host-x", "lease_1", "echo hi");

        Assert.Equal("hi\n", result.Stdout);
        Assert.Equal(("host-x", "lease_1", "echo hi"), a.Local.ExecCalls.Single());
    }

    [Fact]
    public async Task Release_on_a_remote_instance_is_routed_to_the_owner()
    {
        var a = await StartInstanceAsync("A");
        var b = await StartInstanceAsync("B");
        await _presence.SetOwnerAsync("host-x", "A");

        await b.Relay.ReleaseAsync("host-x", "lease_1");

        Assert.Equal(("host-x", "lease_1"), a.Local.ReleaseCalls.Single());
    }

    [Fact]
    public async Task A_locally_owned_host_is_driven_directly_without_routing()
    {
        var a = await StartInstanceAsync("A");
        a.Registry.MarkLocal("host-x");
        await _presence.SetOwnerAsync("host-x", "A");

        var result = await a.Relay.CreateLeaseAsync("host-x", Spec());

        Assert.Equal(a.Local.LeaseResult.LeaseId, result.LeaseId);
        Assert.Single(a.Local.CreateCalls);
    }

    [Fact]
    public async Task Unknown_host_is_host_offline()
    {
        var b = await StartInstanceAsync("B");

        var ex = await Assert.ThrowsAsync<ApiException>(() => b.Relay.CreateLeaseAsync("ghost", Spec()));
        Assert.Equal(ApiErrorCode.HostOffline, ex.Code);
    }

    [Fact]
    public async Task An_owner_side_error_round_trips_with_its_code()
    {
        var a = await StartInstanceAsync("A");
        var b = await StartInstanceAsync("B");
        await _presence.SetOwnerAsync("host-x", "A");
        a.Local.CreateError = new ApiException(ApiErrorCode.LeaseFailed, "image pull failed");

        var ex = await Assert.ThrowsAsync<ApiException>(() => b.Relay.CreateLeaseAsync("host-x", Spec()));

        Assert.Equal(ApiErrorCode.LeaseFailed, ex.Code);
        Assert.Contains("image pull failed", ex.Message);
    }

    // ----- bridged byte streams -------------------------------------------------------------------

    [Fact]
    public async Task Streamed_exec_output_is_bridged_back_to_the_caller_instance()
    {
        var a = await StartInstanceAsync("A");
        var b = await StartInstanceAsync("B");
        await _presence.SetOwnerAsync("host-x", "A");
        a.Local.ExecStreamToReturn = new FakeTunnelExec(
            new[]
            {
                new ExecChunk(1, Encoding.UTF8.GetBytes("out")),
                new ExecChunk(2, Encoding.UTF8.GetBytes("err")),
            },
            exitCode: 7);

        await using var exec = await b.Relay.OpenExecStreamAsync("host-x", "lease_1", "run");

        var chunks = new List<ExecChunk>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var chunk in exec.Output.ReadAllAsync(cts.Token))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal("out", Encoding.UTF8.GetString(chunks[0].Data));
        Assert.Equal((byte)1, chunks[0].Channel);
        Assert.Equal("err", Encoding.UTF8.GetString(chunks[1].Data));
        Assert.Equal((byte)2, chunks[1].Channel);

        await exec.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, exec.ExitCode);
        Assert.Equal(("host-x", "lease_1", "run"), a.Local.ExecStreamCalls.Single());
    }

    [Fact]
    public async Task Shell_is_bridged_duplex_across_instances_with_credit_flowing_back()
    {
        var a = await StartInstanceAsync("A");
        var b = await StartInstanceAsync("B");
        await _presence.SetOwnerAsync("host-x", "A");
        var ownerShell = new FakeTunnelShell(sid: 42);
        a.Local.ShellToReturn = ownerShell;

        await using var shell = await b.Relay.OpenShellAsync("host-x", "lease_1", cols: 80, rows: 24);
        Assert.Equal(42u, shell.Sid);
        Assert.Equal(("host-x", "lease_1", 80, 24), a.Local.ShellCalls.Single());

        // owner → caller: PTY output surfaces on the caller-side handle.
        ownerShell.PushOutput(Encoding.UTF8.GetBytes("hello"));
        var output = await ReadOneAsync(shell.Output);
        Assert.Equal("hello", Encoding.UTF8.GetString(output));

        // caller → owner: keystrokes, resize and drain-credit are forwarded to the owner-side shell.
        await shell.WriteStdinAsync(Encoding.UTF8.GetBytes("ls\n"));
        await shell.ResizeAsync(120, 40);
        await shell.AckOutputDrainedAsync(5);
        await WaitUntilAsync(() =>
            ownerShell.StdinWrites.Count == 1 &&
            ownerShell.LastResize == (120, 40) &&
            ownerShell.DrainedBytes == 5);
        Assert.Equal("ls\n", Encoding.UTF8.GetString(ownerShell.StdinWrites[0]));

        // caller close tears the owner-side shell down.
        await shell.CloseAsync("done");
        await WaitUntilAsync(() => ownerShell.Closed);
    }

    // ----- helpers --------------------------------------------------------------------------------

    private static async Task<byte[]> ReadOneAsync(ChannelReader<byte[]> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await reader.ReadAsync(cts.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "condition not met before the deadline");
    }
}
