using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Wisper.Api.Leases;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Wiring for the multi-instance backplane (docs/DESIGN.md §7, docs/TUNNEL.md §11). Registers the live
/// tunnel registry + relay in one of two shapes, chosen by <see cref="BackplaneOptions.Enabled"/>:
/// <list type="bullet">
/// <item><b>disabled (default)</b> — the single-instance <see cref="InMemoryHostRegistry"/> +
/// <see cref="TunnelRelay"/> back the <see cref="IHostRegistry"/>/<see cref="ITunnelRelay"/> interfaces
/// directly, and nothing touches Redis. This is what Grunt builds and every existing unit test uses.</item>
/// <item><b>enabled</b> — the local registry/relay still own the physical sockets, but the interfaces are
/// fronted by <see cref="DistributedHostRegistry"/> (writes host→instance presence) and
/// <see cref="DistributedTunnelRelay"/> (routes a consumer call to whichever instance owns the host's
/// tunnel). The pub/sub fabric + presence store are Redis-backed when a connection string is configured,
/// or the in-process loopback otherwise (single-process dev — no Redis required).</item>
/// </list>
/// Consumers of <see cref="IHostRegistry"/>/<see cref="ITunnelRelay"/> are oblivious to which shape is
/// active; the interfaces are identical.
/// </summary>
public static class BackplaneServiceCollectionExtensions
{
    public static IServiceCollection AddTunnelBackplane(
        this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(BackplaneOptions.SectionName);
        services.Configure<BackplaneOptions>(section);
        var options = section.Get<BackplaneOptions>() ?? new BackplaneOptions();

        // The local, socket-owning implementations. Always registered as their concrete types: even in
        // distributed mode the physical tunnel for a host lives on exactly one instance, and that instance
        // drives it through these. In single-instance mode they also back the interfaces directly.
        services.AddSingleton<InMemoryHostRegistry>();
        services.AddSingleton<TunnelRelay>();

        if (!options.Enabled)
        {
            // Single-instance default (docs/DESIGN.md §7: "Not required until >1 instance runs").
            services.AddSingleton<IHostRegistry>(sp => sp.GetRequiredService<InMemoryHostRegistry>());
            services.AddSingleton<ITunnelRelay>(sp => sp.GetRequiredService<TunnelRelay>());
            // Shell tickets only need to survive to the WS handshake on the same instance.
            services.AddSingleton<IShellTicketStore, InMemoryShellTicketStore>();
            // Presence store is always needed (CatalogService/HostService consult it for distributed liveness).
            // In single-instance mode the local registry is authoritative, so this store stays empty and the
            // fast-path (local registry) always wins — no Redis I/O.
            services.AddSingleton<IHostPresenceStore, InMemoryHostPresenceStore>();
            return services;
        }

        // ----- distributed mode -----------------------------------------------------------------

        // A stable id for this instance: presence records point at it and its RPC channels are named after
        // it. Blank config → a random id (fine for ephemeral autoscaled pods).
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BackplaneOptions>>().Value;
            var id = string.IsNullOrWhiteSpace(opts.InstanceId)
                ? "wisper-" + Guid.NewGuid().ToString("N")
                : opts.InstanceId!;
            return new WisperInstanceIdentity(id);
        });

        if (!string.IsNullOrWhiteSpace(options.RedisConfiguration))
        {
            // Real Redis fabric + presence (verified against a live Redis separately — Grunt never sets a
            // connection string, so this path is not exercised by the build/test).
            services.TryAddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(options.RedisConfiguration!));
            services.AddSingleton<ITunnelBackplane, RedisTunnelBackplane>();
            services.AddSingleton<IHostPresenceStore>(sp => new RedisHostPresenceStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IOptions<BackplaneOptions>>().Value));
            // Shell tickets stored in Redis so any instance can redeem a ticket minted by another instance.
            // Reuses the backplane's existing IConnectionMultiplexer — no second Redis connection.
            services.AddSingleton<IShellTicketStore>(sp => new RedisShellTicketStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IOptions<BackplaneOptions>>().Value));
        }
        else
        {
            // In-process fabric + presence: a single multi-instance-shaped process (dev/test) with no Redis.
            services.AddSingleton<ITunnelBackplane, LoopbackTunnelBackplane>();
            services.AddSingleton<IHostPresenceStore, InMemoryHostPresenceStore>();
            // Loopback mode is single-process: all "instances" share one DI container, so in-memory is fine.
            services.AddSingleton<IShellTicketStore, InMemoryShellTicketStore>();
        }

        // Front the interfaces with the distributed wrappers, each delegating to the concrete local impl.
        services.AddSingleton<IHostRegistry>(sp => new DistributedHostRegistry(
            sp.GetRequiredService<InMemoryHostRegistry>(),
            sp.GetRequiredService<IHostPresenceStore>(),
            sp.GetRequiredService<WisperInstanceIdentity>(),
            sp.GetRequiredService<ILogger<DistributedHostRegistry>>()));

        // Passed the concrete local relay explicitly — resolving ITunnelRelay here would loop back to this
        // wrapper. The local relay in turn resolves IHostRegistry (the distributed wrapper) whose lookups
        // all delegate to the same InMemoryHostRegistry, so the socket authority stays consistent.
        services.AddSingleton(sp => new DistributedTunnelRelay(
            sp.GetRequiredService<TunnelRelay>(),
            sp.GetRequiredService<IHostRegistry>(),
            sp.GetRequiredService<IHostPresenceStore>(),
            sp.GetRequiredService<ITunnelBackplane>(),
            sp.GetRequiredService<WisperInstanceIdentity>(),
            sp.GetRequiredService<IOptions<BackplaneOptions>>(),
            sp.GetRequiredService<ILogger<DistributedTunnelRelay>>()));
        services.AddSingleton<ITunnelRelay>(sp => sp.GetRequiredService<DistributedTunnelRelay>());
        // Same singleton runs as a hosted service so it subscribes to its RPC request/reply channels at
        // startup and tears them down on shutdown.
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DistributedTunnelRelay>());

        return services;
    }
}
