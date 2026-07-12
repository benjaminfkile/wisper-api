using System.Threading.Tasks;
using Wisper.Api.Audit;
using Wisper.Api.Persistence.Audit;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Audit;

/// <summary>
/// Unit tests for <see cref="AuditService"/> (docs/DATA_MODEL.md §12): recording an action stamps the
/// service clock, serializes the meta object to the jsonb column, and persists the actor/target so the
/// trail is queryable.
/// </summary>
public class AuditServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static (AuditService Service, InMemoryAuditLogRepository Repo) NewService()
    {
        var repo = new InMemoryAuditLogRepository();
        return (new AuditService(repo, new FakeTimeProvider(T0)), repo);
    }

    [Fact]
    public async Task Record_stamps_the_clock_and_serializes_meta()
    {
        var (svc, _) = NewService();
        var admin = Guid.NewGuid();
        var host = Guid.NewGuid();

        var entry = await svc.RecordAsync(
            "host.suspend",
            actorUserId: admin,
            targetType: "host",
            targetId: host,
            meta: new { reason = "abuse", before = "online", after = "suspended" });

        Assert.Equal("host.suspend", entry.Action);
        Assert.Equal(admin, entry.ActorUserId);
        Assert.Equal(host, entry.TargetId);
        Assert.Equal(T0, entry.CreatedAt);
        Assert.NotNull(entry.Meta);
        Assert.Contains("\"reason\":\"abuse\"", entry.Meta!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Record_without_meta_stores_null()
    {
        var (svc, _) = NewService();

        var entry = await svc.RecordAsync("policy.update", actorUserId: Guid.NewGuid());

        Assert.Null(entry.Meta);
    }

    [Fact]
    public async Task Recorded_entry_is_queryable_by_target()
    {
        var (svc, repo) = NewService();
        var host = Guid.NewGuid();

        await svc.RecordAsync("host.suspend", targetType: "host", targetId: host);

        var rows = await repo.ListByTargetAsync("host", host);
        Assert.Single(rows);
    }

    [Fact]
    public async Task Blank_action_is_rejected()
    {
        var (svc, _) = NewService();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RecordAsync(" "));
    }
}
