using System.Text.Json;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Wire-shape tests for the <c>lease.create</c> frame DTO (docs/TUNNEL.md §5): the optional,
/// opaque <c>env</c> map serializes under the <c>env</c> key when present and is omitted entirely
/// when null (matching the omit-when-null pattern the other frame fields use).
/// </summary>
public class LeaseCreateTests
{
    [Fact]
    public void Env_serializes_under_the_env_key_when_present()
    {
        var frame = new LeaseCreate
        {
            Image = "alpine",
            Env = new Dictionary<string, string> { ["FOO"] = "bar", ["BAZ"] = "qux" },
        };

        var json = ControlJson.Serialize(frame);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.True(root.TryGetProperty("env", out var env));
        Assert.Equal("bar", env.GetProperty("FOO").GetString());
        Assert.Equal("qux", env.GetProperty("BAZ").GetString());
    }

    [Fact]
    public void Env_is_omitted_from_the_wire_when_null()
    {
        var frame = new LeaseCreate { Image = "alpine" };

        var json = ControlJson.Serialize(frame);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.False(root.TryGetProperty("env", out _));
    }

    [Fact]
    public void Isolation_serializes_under_the_isolation_key()
    {
        var frame = new LeaseCreate { Image = "alpine", Isolation = "vm" };

        var json = ControlJson.Serialize(frame);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.True(root.TryGetProperty("isolation", out var isolation));
        Assert.Equal("vm", isolation.GetString());
    }

    [Fact]
    public void Isolation_defaults_to_shared_on_the_wire()
    {
        var frame = new LeaseCreate { Image = "alpine" };

        var json = ControlJson.Serialize(frame);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.True(root.TryGetProperty("isolation", out var isolation));
        Assert.Equal("shared", isolation.GetString());
    }

    [Fact]
    public void Isolation_round_trips_through_deserialization()
    {
        var frame = new LeaseCreate { Isolation = "sandboxed" };

        var back = ControlJson.Deserialize<LeaseCreate>(ControlJson.Serialize(frame));

        Assert.NotNull(back);
        Assert.Equal("sandboxed", back!.Isolation);
    }

    [Fact]
    public void Resources_gpus_serializes_under_the_gpus_key()
    {
        var frame = new LeaseCreate
        {
            Image = "alpine",
            Resources = new LeaseResources { Cpus = 2, MemoryMb = 4096, Pids = 1024, Gpus = 2 },
        };

        var json = ControlJson.Serialize(frame);
        var resources = JsonDocument.Parse(json).RootElement.GetProperty("resources");

        Assert.Equal(2, resources.GetProperty("gpus").GetInt32());
    }

    [Fact]
    public void Resources_gpus_is_omitted_from_the_wire_when_zero()
    {
        // Like cpus/memory_mb/pids, a zero gpus count is omitted (WhenWritingDefault) — the agent/wisp then
        // apply their own default. Only a non-zero booked count travels on the frame.
        var frame = new LeaseCreate { Image = "alpine", Resources = new LeaseResources { Cpus = 2 } };

        var json = ControlJson.Serialize(frame);
        var resources = JsonDocument.Parse(json).RootElement.GetProperty("resources");

        Assert.False(resources.TryGetProperty("gpus", out _));
    }

    [Fact]
    public void Env_round_trips_through_deserialization()
    {
        var frame = new LeaseCreate
        {
            Env = new Dictionary<string, string> { ["TOKEN"] = "s3cr3t" },
        };

        var back = ControlJson.Deserialize<LeaseCreate>(ControlJson.Serialize(frame));

        Assert.NotNull(back);
        Assert.NotNull(back!.Env);
        Assert.Equal("s3cr3t", back.Env!["TOKEN"]);
    }
}
