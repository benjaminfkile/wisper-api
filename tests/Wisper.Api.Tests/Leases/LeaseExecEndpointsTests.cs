using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Leases;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Idempotency;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Messages;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Leases;

/// <summary>
/// Integration tests over the real app host for consumer exec (docs/API.md §5, §7): the sync
/// <c>POST /v1/leases/:id/exec</c> → <c>{stdout,stderr,exit_code}</c> and the <c>?stream=1</c> SSE
/// (<c>chunk</c>/<c>exit</c>/<c>error</c>), the ownership (404) and ready (409 <c>lease_not_ready</c>)
/// checks via the uniform envelope, and the reuse of the relay's exec / exec-stream paths. The JWT
/// validator, repositories and the tunnel relay are swapped for in-memory doubles (Grunt has no
/// Cognito/Postgres/tunnel).
/// </summary>
public class LeaseExecEndpointsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryHostImageRepository Images { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryIdempotencyKeyRepository Idempotency { get; } = new();
        public FakeTunnelRelay Relay { get; } = new();
        public FakeJwtValidator Validator { get; } = new();

        public Host? Host { get; private set; }
        public HostImage? Image { get; private set; }

        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IJwtValidator>();
                    services.AddSingleton<IJwtValidator>(Validator);
                    services.RemoveAll<ILeaseRepository>();
                    services.AddSingleton<ILeaseRepository>(Leases);
                    services.RemoveAll<IHostRepository>();
                    services.AddSingleton<IHostRepository>(Hosts);
                    services.RemoveAll<IHostImageRepository>();
                    services.AddSingleton<IHostImageRepository>(Images);
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton<IUserRepository>(Users);
                    services.RemoveAll<IIdempotencyKeyRepository>();
                    services.AddSingleton<IIdempotencyKeyRepository>(Idempotency);
                    services.RemoveAll<ITunnelRelay>();
                    services.AddSingleton<ITunnelRelay>(Relay);
                }));

        public async Task SeedImageAsync()
        {
            var host = await Hosts.CreateAsync(new Host
            {
                Id = Guid.NewGuid(),
                OwnerUserId = Guid.NewGuid(),
                Name = "home-server-1",
                Label = "us",
                Status = HostStatus.Online,
                AgentTokenHash = "hash",
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            Host = host;
            Image = await Images.CreateAsync(new HostImage
            {
                HostId = host.Id,
                ImageRef = "reg/wisp-base:latest",
                PriceCentsPerMin = 5,
                Networks = new[] { NetworkMode.None, NetworkMode.Open },
                MaxTtlSeconds = 14400,
                MaxCpus = 4,
                MaxMemoryMb = 8192,
                MaxPids = 1024,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
        }

        /// <summary>Creates a lease in the given state, owned by <paramref name="consumerUserId"/>.</summary>
        public async Task<string> SeedLeaseAsync(Guid consumerUserId, LeaseStatus status = LeaseStatus.Active)
        {
            var id = Guid.NewGuid();
            await Leases.CreateAsync(new Lease
            {
                Id = id,
                ConsumerUserId = consumerUserId,
                HostId = Host!.Id,
                HostImageId = Image!.Id,
                ImageRef = Image.ImageRef,
                Network = NetworkMode.Open,
                TtlSeconds = 3600,
                PriceCentsPerMin = 5,
                Currency = "usd",
                Status = status,
                WispContractId = "wisp-contract-1",
                CreatedAt = T0,
                StartedAt = status == LeaseStatus.Provisioning ? null : T0,
                LastMeteredAt = T0,
                BillableSeconds = 0,
            });
            return TunnelLeaseId.Format(id);
        }

        /// <summary>The users row the JWT bootstraps on first authenticated call.</summary>
        public async Task<Guid> CallerIdAsync() =>
            (await Users.GetByCognitoSubAsync("fake-sub"))!.Id;
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    /// <summary>Bootstraps the caller's users row (via /v1/me) and returns its id, so a lease can be owned by it.</summary>
    private static async Task<Guid> BootstrapCallerAsync(WebApplicationFactory<Program> factory, Fixture fx)
    {
        var me = await Authed(factory).GetAsync("/v1/me");
        me.EnsureSuccessStatusCode();
        return await fx.CallerIdAsync();
    }

    [Fact]
    public async Task Exec_without_a_token_is_401()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();

        var response = await factory.CreateClient()
            .PostAsync($"/v1/leases/lease_{Guid.NewGuid():N}/exec", JsonContent.Create(new { command = "ls" }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Exec_sync_returns_stdout_stderr_and_exit_code()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);
        fx.Relay.SyncExecResult = new ExecResult { Stdout = "hello\n", Stderr = "warn\n", ExitCode = 3 };

        var response = await Authed(factory)
            .PostAsync($"/v1/leases/{leaseId}/exec", JsonContent.Create(new { command = "echo hello" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ExecDto>();
        Assert.Equal("hello\n", body!.Stdout);
        Assert.Equal("warn\n", body.Stderr);
        Assert.Equal(3, body.ExitCode);

        // Reused the relay's sync exec path, addressed by (host id, lease token).
        var call = Assert.Single(fx.Relay.ExecCalls);
        Assert.Equal(fx.Host!.Id.ToString(), call.HostId);
        Assert.Equal(leaseId, call.LeaseId);
        Assert.Equal("echo hello", call.Command);
    }

    [Fact]
    public async Task Exec_unknown_lease_is_404()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        await BootstrapCallerAsync(factory, fx);

        var response = await Authed(factory)
            .PostAsync($"/v1/leases/lease_{Guid.NewGuid():N}/exec", JsonContent.Create(new { command = "ls" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fx.Relay.ExecCalls);
    }

    [Fact]
    public async Task Exec_malformed_lease_id_is_404()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        await BootstrapCallerAsync(factory, fx);

        var response = await Authed(factory)
            .PostAsync("/v1/leases/not-a-lease/exec", JsonContent.Create(new { command = "ls" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Exec_on_a_lease_owned_by_another_user_is_404()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        await BootstrapCallerAsync(factory, fx);
        var otherUsersLease = await fx.SeedLeaseAsync(Guid.NewGuid());

        var response = await Authed(factory)
            .PostAsync($"/v1/leases/{otherUsersLease}/exec", JsonContent.Create(new { command = "ls" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fx.Relay.ExecCalls);
    }

    [Fact]
    public async Task Exec_before_the_lease_is_active_is_409_lease_not_ready()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller, LeaseStatus.Provisioning);

        var response = await Authed(factory)
            .PostAsync($"/v1/leases/{leaseId}/exec", JsonContent.Create(new { command = "ls" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("lease_not_ready", envelope!.Error.Code);
        Assert.Empty(fx.Relay.ExecCalls);
    }

    [Fact]
    public async Task Exec_surfaces_host_offline_as_409()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);
        fx.Relay.ExecError = new Wisper.Api.Infrastructure.ApiException(
            Wisper.Api.Infrastructure.ApiErrorCode.HostOffline, "no live tunnel");

        var response = await Authed(factory)
            .PostAsync($"/v1/leases/{leaseId}/exec", JsonContent.Create(new { command = "ls" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("host_offline", envelope!.Error.Code);
    }

    [Fact]
    public async Task Exec_stream_yields_chunk_events_then_exit()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);
        fx.Relay.ExecStream = new FakeTunnelExec(
            new[]
            {
                new ExecChunk(Channels.Stdout, Encoding.UTF8.GetBytes("build starting\n")),
                new ExecChunk(Channels.Stderr, Encoding.UTF8.GetBytes("warning\n")),
            },
            exitCode: 0);

        var events = await PostStreamExecAsync(factory, leaseId, "make build");

        Assert.Equal(3, events.Count);
        Assert.Equal("chunk", events[0].Event);
        Assert.Equal("stdout", events[0].Data.GetProperty("stream").GetString());
        Assert.Equal("build starting\n", events[0].Data.GetProperty("data").GetString());
        Assert.Equal("chunk", events[1].Event);
        Assert.Equal("stderr", events[1].Data.GetProperty("stream").GetString());
        Assert.Equal("warning\n", events[1].Data.GetProperty("data").GetString());
        Assert.Equal("exit", events[2].Event);
        Assert.Equal(0, events[2].Data.GetProperty("exit_code").GetInt32());

        // Reused the relay's exec-stream path; the relay drained the output and tore the stream down.
        var call = Assert.Single(fx.Relay.ExecStreamCalls);
        Assert.Equal(leaseId, call.LeaseId);
        Assert.Equal("make build", call.Command);
        Assert.True(fx.Relay.ExecStream.Closed);
        Assert.Equal(Encoding.UTF8.GetByteCount("build starting\n") + Encoding.UTF8.GetByteCount("warning\n"),
            fx.Relay.ExecStream.DrainedBytes);
    }

    [Fact]
    public async Task Exec_stream_propagates_a_nonzero_exit_code()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);
        fx.Relay.ExecStream = new FakeTunnelExec(
            new[] { new ExecChunk(Channels.Stderr, Encoding.UTF8.GetBytes("boom\n")) }, exitCode: 17);

        var events = await PostStreamExecAsync(factory, leaseId, "false");

        Assert.Equal(2, events.Count);
        Assert.Equal("chunk", events[0].Event);
        Assert.Equal("exit", events[1].Event);
        Assert.Equal(17, events[1].Data.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task Exec_stream_reports_an_open_failure_as_an_error_event()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller);
        fx.Relay.ExecStreamError = new Wisper.Api.Infrastructure.ApiException(
            Wisper.Api.Infrastructure.ApiErrorCode.HostOffline, "no live tunnel");

        var events = await PostStreamExecAsync(factory, leaseId, "make build");

        var error = Assert.Single(events);
        Assert.Equal("error", error.Event);
        Assert.Equal("host_offline", error.Data.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exec_stream_before_the_lease_is_active_is_409_not_an_sse_stream()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        using var factory = fx.Build();
        var caller = await BootstrapCallerAsync(factory, fx);
        var leaseId = await fx.SeedLeaseAsync(caller, LeaseStatus.Provisioning);

        var response = await Authed(factory)
            .PostAsync($"/v1/leases/{leaseId}/exec?stream=1", JsonContent.Create(new { command = "ls" }));

        // The ready check runs before the SSE response starts, so this is the uniform 409 envelope.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.Equal("lease_not_ready", envelope!.Error.Code);
        Assert.Empty(fx.Relay.ExecStreamCalls);
    }

    private static async Task<List<SseEvent>> PostStreamExecAsync(
        WebApplicationFactory<Program> factory, string leaseId, string command)
    {
        var client = Authed(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/leases/{leaseId}/exec?stream=1")
        {
            Content = JsonContent.Create(new { command }),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var payload = await response.Content.ReadAsStreamAsync();
        return await ParseSseAsync(payload);
    }

    private static async Task<List<SseEvent>> ParseSseAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var events = new List<SseEvent>();
        string? name = null;
        string? data = null;

        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (name is not null && data is not null)
                {
                    events.Add(new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone()));
                }

                name = null;
                data = null;
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line["data: ".Length..];
            }
        }

        return events;
    }

    private readonly record struct SseEvent(string Event, JsonElement Data);

    private sealed record ExecDto(
        [property: JsonPropertyName("stdout")] string Stdout,
        [property: JsonPropertyName("stderr")] string Stderr,
        [property: JsonPropertyName("exit_code")] int ExitCode);

    private sealed record ErrorEnvelopeDto(
        [property: JsonPropertyName("error")] ErrorBodyDto Error);

    private sealed record ErrorBodyDto(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);
}
