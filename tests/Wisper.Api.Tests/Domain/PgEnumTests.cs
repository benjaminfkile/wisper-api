using Wisper.Api.Domain;
using Xunit;

namespace Wisper.Api.Tests.Domain;

/// <summary>
/// The <see cref="PgEnum"/> label mapping the Dapper repositories rely on (docs/DATA_MODEL.md §2).
/// Single-word enums use the lowercase pair; the multi-word <c>lease_end_reason</c> needs the
/// snake_case pair, which is what lets <c>host_disconnect</c> round-trip to
/// <see cref="LeaseEndReason.HostDisconnect"/> (Dapper's built-in enum parse can't bridge the underscore).
/// </summary>
public class PgEnumTests
{
    [Theory]
    [InlineData(LeaseStatus.Pending, "pending")]
    [InlineData(LeaseStatus.Provisioning, "provisioning")]
    [InlineData(LeaseStatus.Suspended, "suspended")]
    [InlineData(LeaseStatus.Ended, "ended")]
    public void Single_word_lease_status_uses_the_lowercase_label(LeaseStatus status, string label)
    {
        Assert.Equal(label, PgEnum.ToLabel(status));
        Assert.Equal(status, PgEnum.Parse<LeaseStatus>(label));
    }

    [Theory]
    [InlineData(LeaseEndReason.Released, "released")]
    [InlineData(LeaseEndReason.Expired, "expired")]
    [InlineData(LeaseEndReason.HostDisconnect, "host_disconnect")]
    [InlineData(LeaseEndReason.ContainerLost, "container_lost")]
    [InlineData(LeaseEndReason.Admin, "admin")]
    [InlineData(LeaseEndReason.PaymentFailed, "payment_failed")]
    public void Lease_end_reason_round_trips_through_snake_case_labels(LeaseEndReason reason, string label)
    {
        Assert.Equal(label, PgEnum.ToSnakeLabel(reason));
        Assert.Equal(reason, PgEnum.ParseSnake<LeaseEndReason>(label));
    }
}
