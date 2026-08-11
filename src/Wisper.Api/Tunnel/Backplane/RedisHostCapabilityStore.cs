using System.Text.Json;
using StackExchange.Redis;
using Wisper.Api.Domain;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Redis <see cref="IHostCapabilityStore"/>: capability snapshots live in a single hash
/// <c>{prefix}:capability</c> mapping <c>hostId → JSON</c> (task #17, docs/DESIGN.md §7). The lifecycle
/// is tied to presence: <see cref="SetAsync"/> is called on host register and <see cref="ClearAsync"/>
/// on unregister, so a stale snapshot can never outlive its tunnel. <see cref="Get"/> uses the
/// StackExchange.Redis synchronous API so <see cref="IHostCapabilitySource.GetCapability"/> stays sync.
/// </summary>
public sealed class RedisHostCapabilityStore : IHostCapabilityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisKey _key;

    public RedisHostCapabilityStore(IConnectionMultiplexer multiplexer, BackplaneOptions options)
    {
        _multiplexer = multiplexer;
        _key = $"{options.ChannelPrefix}:capability";
    }

    private IDatabase Db => _multiplexer.GetDatabase();

    public Task SetAsync(string hostId, HostCapabilitySnapshot snapshot, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(ToDto(snapshot), JsonOptions);
        return Db.HashSetAsync(_key, hostId, json);
    }

    public HostCapabilitySnapshot? Get(string hostId)
    {
        var value = Db.HashGet(_key, hostId);
        if (value.IsNullOrEmpty) return null;
        var dto = JsonSerializer.Deserialize<CapabilityDto>(value.ToString(), JsonOptions);
        return dto is null ? null : FromDto(dto);
    }

    public Task ClearAsync(string hostId, CancellationToken ct = default) =>
        Db.HashDeleteAsync(_key, (RedisValue)hostId);

    private static CapabilityDto ToDto(HostCapabilitySnapshot s) => new(
        s.Images.ToArray(),
        s.Networks.Select(n => n.ToString()).ToArray(),
        s.MaxTtlSeconds,
        s.MaxCpus,
        s.MaxMemoryMb,
        s.MaxPids,
        s.Os,
        s.MaxGpus,
        s.GpuClasses.ToArray(),
        s.MaxContracts);

    private static HostCapabilitySnapshot FromDto(CapabilityDto dto)
    {
        var networks = new List<NetworkMode>(dto.Networks.Length);
        foreach (var label in dto.Networks)
        {
            if (Enum.TryParse<NetworkMode>(label, ignoreCase: true, out var mode) && Enum.IsDefined(mode))
            {
                networks.Add(mode);
            }
        }

        return new HostCapabilitySnapshot(
            dto.Images,
            networks,
            dto.MaxTtlSeconds,
            dto.MaxCpus,
            dto.MaxMemoryMb,
            dto.MaxPids,
            dto.Os,
            dto.MaxGpus,
            dto.GpuClasses,
            dto.MaxContracts);
    }

    /// <summary>Compact serialization DTO — plain arrays, networks stored as their string labels.</summary>
    private sealed record CapabilityDto(
        string[] Images,
        string[] Networks,
        long MaxTtlSeconds,
        double MaxCpus,
        long MaxMemoryMb,
        long MaxPids,
        string? Os,
        int MaxGpus,
        string[] GpuClasses,
        int MaxContracts);
}
