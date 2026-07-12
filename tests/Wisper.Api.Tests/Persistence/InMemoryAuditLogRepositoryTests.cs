using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Audit;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="IAuditLogRepository"/> against the in-memory double (Grunt has no
/// Postgres). Covers append with a server-assigned monotonic id, and the target/actor listings
/// newest-first (docs/DATA_MODEL.md §12).
/// </summary>
public class InMemoryAuditLogRepositoryTests
{
    private static AuditLogEntry NewEntry(string action, Guid? actor = null, string? targetType = null, Guid? targetId = null) => new()
    {
        Action = action,
        ActorUserId = actor,
        TargetType = targetType,
        TargetId = targetId,
    };

    [Fact]
    public async Task Append_assigns_a_monotonic_id_and_timestamp()
    {
        var repo = new InMemoryAuditLogRepository();

        var first = await repo.AppendAsync(NewEntry("policy.update"));
        var second = await repo.AppendAsync(NewEntry("host.suspend"));

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.NotEqual(default, first.CreatedAt);
    }

    [Fact]
    public async Task ListByTarget_scopes_and_orders_newest_first()
    {
        var repo = new InMemoryAuditLogRepository();
        var host = Guid.NewGuid();
        await repo.AppendAsync(NewEntry("host.suspend", targetType: "host", targetId: host));
        await repo.AppendAsync(NewEntry("host.resume", targetType: "host", targetId: host));
        await repo.AppendAsync(NewEntry("host.suspend", targetType: "host", targetId: Guid.NewGuid()));

        var rows = await repo.ListByTargetAsync("host", host);

        Assert.Equal(new[] { "host.resume", "host.suspend" }, rows.Select(e => e.Action).ToArray());
    }

    [Fact]
    public async Task ListByActor_scopes_to_the_actor()
    {
        var repo = new InMemoryAuditLogRepository();
        var admin = Guid.NewGuid();
        await repo.AppendAsync(NewEntry("ledger.adjustment", actor: admin));
        await repo.AppendAsync(NewEntry("payout.trigger", actor: Guid.NewGuid()));

        var rows = await repo.ListByActorAsync(admin);

        Assert.Single(rows);
        Assert.Equal("ledger.adjustment", rows[0].Action);
    }
}
