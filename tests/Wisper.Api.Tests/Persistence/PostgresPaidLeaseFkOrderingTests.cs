using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Leases;
using Wisper.Api.Metering;
using Wisper.Api.Persistence;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel.Backplane;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The regression that would have caught task #540: the paid-lease create driven end-to-end against the
/// <b>real</b> Postgres-backed <see cref="LedgerStore"/> + <see cref="LeaseRepository"/> on a throwaway
/// server (<see cref="EphemeralPostgres"/>), rather than the in-memory doubles the rest of the suite uses.
/// The bug — posting the wallet hold (which carries <c>lease_id</c>, FK → <c>leases.id</c>) <i>before</i> the
/// lease row is persisted — is invisible in-memory (no FK) and only trips
/// <c>ledger_transactions_lease_id_fkey</c> against SQL. These tests assert (a) the reordered create posts
/// the hold and persists both the lease row and the ledger transaction, and (b) the store really does
/// enforce that FK, so the pre-fix ordering would have failed with the exact <c>23503</c> from the report.
/// <para>
/// These stand up a real server, so they are gated behind an explicit <c>WISPER_RUN_PG_TESTS</c> opt-in
/// (task #558). When it is unset — the default, including normal CI — the fixture reports unavailable and each
/// test is reported <b>skipped</b> (a visible <c>[SkippableFact]</c> skip, not a hidden no-op), so the suite
/// stays deterministically green regardless of whatever Postgres the runner happens to ship. Set
/// <c>WISPER_RUN_PG_TESTS=1</c> to run the regression for real (this repo ships PostgreSQL 15); see
/// <see cref="EphemeralPostgres"/> and the README's "Develop" section.
/// </para>
/// </summary>
public sealed class PostgresPaidLeaseFkOrderingTests : IClassFixture<PostgresPaidLeaseFkOrderingTests.PgFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly PgFixture _pg;

    public PostgresPaidLeaseFkOrderingTests(PgFixture pg) => _pg = pg;

    [SkippableFact]
    public async Task Paid_lease_create_posts_the_hold_and_persists_both_the_lease_row_and_the_ledger_txn()
    {
        Skip.IfNot(_pg.Available, PgFixture.SkipReason); // visible skip unless WISPER_RUN_PG_TESTS opted in

        var env = new Harness(_pg.DataSource!);
        var (consumerId, host, image) = await env.SeedPricedOfferAsync(priceCentsPerMin: 2);
        await env.FundWalletAsync(consumerId, cents: 100_000);

        // The exact repro: POST /v1/leases against a priced offer (2¢/min, ttl 3600 → hold = 60 * 2 = 120¢).
        // Resources are fixed by the offer now (task #570), so the request carries no resource knobs.
        var request = new CreateLeaseRequest(
            host.Id.ToString(), image.Id.ToString(), "open", null, 3600, "echo hi");

        var created = await env.Service.CreateAsync(consumerId, request);

        // 201 body: the hold snapshot the pre-fix path never reached.
        Assert.Equal(120, created.HoldCents);
        Assert.Equal(LeaseStatus.Active, created.Lease.Status);

        // The lease row is persisted, active, and now carries the hold transaction id.
        var stored = await env.Leases.GetByIdAsync(created.Lease.Id);
        Assert.NotNull(stored);
        Assert.Equal(LeaseStatus.Active, stored!.Status);
        Assert.NotNull(stored.HoldTxnId);

        // The lease_hold ledger transaction exists, is keyed by the lease id, and matches the row's pointer —
        // the FK it carries (lease_id → leases.id) was satisfied because the row was persisted first.
        var holdTxn = await env.LedgerStore.FindTransactionByIdempotencyKeyAsync(
            WalletLeaseGate.HoldIdempotencyKey(created.Lease.Id));
        Assert.NotNull(holdTxn);
        Assert.Equal(LedgerTxnKind.LeaseHold, holdTxn!.Kind);
        Assert.Equal(created.Lease.Id, holdTxn.LeaseId);
        Assert.Equal(holdTxn.Id, stored.HoldTxnId);

        // The wallet was debited exactly the hold; the earmark now sits in lease_holds.
        var wallet = await env.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumerId);
        Assert.Equal(100_000 - 120, wallet.BalanceCents);
    }

    [SkippableFact]
    public async Task The_sql_store_enforces_the_lease_id_fk_so_the_pre_fix_ordering_fails_with_23503()
    {
        Skip.IfNot(_pg.Available, PgFixture.SkipReason);

        var env = new Harness(_pg.DataSource!);
        var (consumerId, _, _) = await env.SeedPricedOfferAsync(priceCentsPerMin: 2);
        await env.FundWalletAsync(consumerId, cents: 100_000);

        var wallet = await env.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumerId);
        var holds = await env.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);

        // The pre-fix ordering: place a hold for a lease id that has no leases row yet. The hold's
        // ledger_transactions.lease_id FK → leases.id trips immediately (23503) — the raw PostgresException
        // the report saw. This is exactly what the reorder (persist the row first) now avoids.
        var ghostLeaseId = Guid.NewGuid();
        var draft = LedgerFlows.LeaseHold(
            wallet.Id, holds.Id, ghostLeaseId, amountCents: 120,
            idempotencyKey: WalletLeaseGate.HoldIdempotencyKey(ghostLeaseId), memo: "pre-fix ordering");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => env.Ledger.PostAsync(draft));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState); // 23503

        // And nothing was persisted — no orphan transaction lingering under that idempotency key.
        Assert.Null(await env.LedgerStore.FindTransactionByIdempotencyKeyAsync(
            WalletLeaseGate.HoldIdempotencyKey(ghostLeaseId)));
    }

    /// <summary>Wires the real SQL repositories/services over the throwaway server for one test.</summary>
    private sealed class Harness
    {
        private readonly Db _db;

        public Harness(NpgsqlDataSource dataSource)
        {
            _db = new Db(dataSource);
            Users = new UserRepository(_db);
            Hosts = new HostRepository(_db);
            Images = new HostImageRepository(_db);
            Leases = new LeaseRepository(_db);
            LedgerStore = new LedgerStore(_db);
            Ledger = new LedgerService(LedgerStore);

            var clock = new FakeTimeProvider(T0);
            var policy = new PlatformPolicyService(new InMemoryPlatformPolicyRepository(), clock);
            var fraud = new FraudGuardService(
                Ledger, Leases, policy, clock, NullLogger<FraudGuardService>.Instance);
            WalletGate = new WalletLeaseGate(
                Ledger, Leases, policy, fraud, NullLogger<WalletLeaseGate>.Instance);
            var usage = new LeaseUsageRepository(_db);
            var meter = new MeteringService(
                Leases, usage, Hosts, Ledger, policy, clock, NullLogger<MeteringService>.Instance);
            Service = new LeaseService(
                Leases, Hosts, Images, new FakeTunnelRelay(), new FakeHostCapabilitySource(),
                WalletGate, meter, policy, new InMemoryHostDegradedStore(), clock);
        }

        public UserRepository Users { get; }
        public HostRepository Hosts { get; }
        public HostImageRepository Images { get; }
        public LeaseRepository Leases { get; }
        public LedgerStore LedgerStore { get; }
        public LedgerService Ledger { get; }
        public WalletLeaseGate WalletGate { get; }
        public LeaseService Service { get; }

        public async Task<(Guid ConsumerId, Host Host, HostImage Image)> SeedPricedOfferAsync(
            long priceCentsPerMin)
        {
            var owner = await Users.CreateAsync(NewUser("owner"));
            var consumer = await Users.CreateAsync(NewUser("consumer"));
            var host = await Hosts.CreateAsync(new Host
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner.Id,
                Name = "home-server-1",
                Label = "us",
                Status = HostStatus.Online,
                AgentTokenHash = "hash",
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            var image = await Images.CreateAsync(new HostImage
            {
                HostId = host.Id,
                ImageRef = "reg/wisp-base:latest",
                PriceCentsPerMin = priceCentsPerMin,
                Networks = new[] { NetworkMode.None, NetworkMode.Open },
                MaxTtlSeconds = 14400,
                MaxCpus = 4,
                MaxMemoryMb = 8192,
                MaxPids = 1024,
                Gpus = 0,
                Enabled = true,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            return (consumer.Id, host, image);
        }

        public async Task FundWalletAsync(Guid consumerId, long cents)
        {
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumerId);
            var platformCash = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var stripeFees = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            await Ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, platformCash.Id, stripeFees.Id,
                grossAmountCents: cents, stripeFeeCents: 0, idempotencyKey: "topup:" + consumerId));
        }

        private static User NewUser(string tag)
        {
            var id = Guid.NewGuid();
            return new User
            {
                Id = id,
                CognitoSub = $"{tag}-{id:N}",
                Email = $"{tag}-{id:N}@example.test",
                Status = UserStatus.Active,
                CreatedAt = T0,
                UpdatedAt = T0,
            };
        }
    }

    /// <summary>
    /// Class fixture: stands up one throwaway server and runs the migrations once for the whole class. Unless
    /// the <c>WISPER_RUN_PG_TESTS</c> opt-in is set it reports <see cref="Available"/> = <c>false</c> and the
    /// tests report a visible skip.
    /// </summary>
    public sealed class PgFixture : IAsyncLifetime
    {
        public const string SkipReason =
            "ephemeral-Postgres regression is opt-in: set WISPER_RUN_PG_TESTS=1 to run it (task #558)";

        private EphemeralPostgres? _server;

        public NpgsqlDataSource? DataSource { get; private set; }

        public bool Available => DataSource is not null;

        public async Task InitializeAsync()
        {
            _server = await EphemeralPostgres.TryStartAsync();
            if (_server is null)
            {
                return;
            }

            // The Dapper snake_case↔PascalCase mapping the repositories rely on (set at app wiring).
            DefaultTypeMap.MatchNamesWithUnderscores = true;

            MigrationRunner.Run(_server.ConnectionString, NullLogger.Instance);
            DataSource = new NpgsqlDataSourceBuilder(_server.ConnectionString).Build();
        }

        public async Task DisposeAsync()
        {
            if (DataSource is not null)
            {
                await DataSource.DisposeAsync();
            }

            if (_server is not null)
            {
                await _server.DisposeAsync();
            }
        }
    }
}
