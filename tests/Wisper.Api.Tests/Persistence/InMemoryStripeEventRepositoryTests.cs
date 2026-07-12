using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Stripe;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="IStripeEventRepository"/> against the in-memory double (Grunt has no
/// Postgres). Covers the exactly-once webhook store: a first sight inserts and reports <c>true</c>, a
/// re-delivery reports <c>false</c> and changes nothing, and handlers advance the row to a terminal state
/// (docs/DATA_MODEL.md §9, docs/PAYMENTS.md §9).
/// </summary>
public class InMemoryStripeEventRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static StripeEvent NewEvent(string id, string type = "payment_intent.succeeded") => new()
    {
        Id = id,
        Type = type,
        Payload = """{"id":"evt_1"}""",
    };

    [Fact]
    public async Task First_insert_wins_and_round_trips()
    {
        var repo = new InMemoryStripeEventRepository();

        Assert.True(await repo.TryInsertReceivedAsync(NewEvent("evt_1")));

        var stored = await repo.GetAsync("evt_1");
        Assert.NotNull(stored);
        Assert.Equal(StripeEventStatus.Received, stored!.Status);
        Assert.Equal("payment_intent.succeeded", stored.Type);
    }

    [Fact]
    public async Task Redelivery_of_the_same_id_is_a_dedupe_no_op()
    {
        var repo = new InMemoryStripeEventRepository();
        await repo.TryInsertReceivedAsync(NewEvent("evt_1", "account.updated"));

        // A re-delivery — even with a different type/payload — must not overwrite the stored row.
        var second = await repo.TryInsertReceivedAsync(NewEvent("evt_1", "transfer.created"));

        Assert.False(second);
        Assert.Equal("account.updated", (await repo.GetAsync("evt_1"))!.Type);
    }

    [Fact]
    public async Task SetStatus_moves_to_a_terminal_state_with_processed_at_and_error()
    {
        var repo = new InMemoryStripeEventRepository();
        await repo.TryInsertReceivedAsync(NewEvent("evt_1"));

        var processed = await repo.SetStatusAsync("evt_1", StripeEventStatus.Processed, T0);
        Assert.Equal(StripeEventStatus.Processed, processed!.Status);
        Assert.Equal(T0, processed.ProcessedAt);

        var failed = await repo.SetStatusAsync("evt_1", StripeEventStatus.Failed, T0, "boom");
        Assert.Equal(StripeEventStatus.Failed, failed!.Status);
        Assert.Equal("boom", failed.Error);
    }

    [Fact]
    public async Task SetStatus_on_unknown_id_returns_null()
    {
        var repo = new InMemoryStripeEventRepository();
        Assert.Null(await repo.SetStatusAsync("nope", StripeEventStatus.Ignored, T0));
        Assert.Null(await repo.GetAsync("nope"));
    }
}
