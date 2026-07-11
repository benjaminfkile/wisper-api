using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Tests for the control-frame envelope (docs/TUNNEL.md §5). The serialized JSON
/// must match the Go agent's <c>omitempty</c> form: <c>rid</c>/<c>sid</c> omitted
/// when 0, present otherwise.
/// </summary>
public class ControlEnvelopeTests
{
    [Fact]
    public void Serialize_omits_rid_and_sid_when_zero()
    {
        var envelope = new ControlEnvelope { T = FrameTypes.HostHeartbeat };

        var json = ControlJson.Serialize(envelope);

        Assert.Equal("{\"t\":\"host.heartbeat\"}", json);
        Assert.DoesNotContain("rid", json);
        Assert.DoesNotContain("sid", json);
    }

    [Fact]
    public void Serialize_includes_rid_and_sid_when_non_zero()
    {
        var envelope = new ControlEnvelope { T = FrameTypes.ShellOpen, Rid = 7, Sid = 3 };

        var json = ControlJson.Serialize(envelope);

        Assert.Equal("{\"t\":\"shell.open\",\"rid\":7,\"sid\":3}", json);
    }

    [Fact]
    public void Serialize_includes_only_the_non_zero_id()
    {
        var envelope = new ControlEnvelope { T = FrameTypes.LeaseReady, Sid = 99 };

        var json = ControlJson.Serialize(envelope);

        Assert.Equal("{\"t\":\"lease.ready\",\"sid\":99}", json);
    }

    [Fact]
    public void RoundTrip_preserves_all_fields()
    {
        var envelope = new ControlEnvelope { T = FrameTypes.ExecOpen, Rid = 0x80000001, Sid = 0xFFFFFFFF };

        var json = ControlJson.Serialize(envelope);
        var back = ControlJson.Deserialize<ControlEnvelope>(json);

        Assert.NotNull(back);
        Assert.Equal(FrameTypes.ExecOpen, back!.T);
        Assert.Equal(0x80000001u, back.Rid);
        Assert.Equal(0xFFFFFFFFu, back.Sid);
    }

    [Fact]
    public void Deserialize_absent_ids_default_to_zero()
    {
        var back = ControlJson.Deserialize<ControlEnvelope>("{\"t\":\"hello\"}");

        Assert.NotNull(back);
        Assert.Equal(FrameTypes.Hello, back!.T);
        Assert.Equal(0u, back.Rid);
        Assert.Equal(0u, back.Sid);
    }

    [Fact]
    public void PeekType_reads_type_without_full_deserialization()
    {
        var json = "{\"t\":\"lease.create\",\"rid\":5,\"leaseId\":\"abc\",\"resources\":{\"cpus\":2}}";

        Assert.Equal("lease.create", ControlJson.PeekType(json));
    }

    [Fact]
    public void PeekType_reads_type_when_not_first_property()
    {
        var json = "{\"rid\":5,\"sid\":3,\"t\":\"exec.opened\"}";

        Assert.Equal("exec.opened", ControlJson.PeekType(json));
    }

    [Fact]
    public void PeekType_returns_null_for_malformed_json()
    {
        Assert.Null(ControlJson.PeekType("not json"));
    }

    [Fact]
    public void PeekType_returns_null_when_t_absent()
    {
        Assert.Null(ControlJson.PeekType("{\"rid\":5,\"sid\":3}"));
    }
}
