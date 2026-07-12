using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The <see cref="Db"/> handle must let the app boot with no database configured (the tunnel needs
/// none), exposing that state without throwing until the data source is actually asked for.
/// </summary>
public class DbTests
{
    [Fact]
    public void Unconfigured_reports_not_configured()
    {
        var db = Db.Unconfigured;

        Assert.False(db.IsConfigured);
        Assert.False(db.TryGetDataSource(out _));
    }

    [Fact]
    public void Unconfigured_DataSource_throws_with_actionable_message()
    {
        var db = Db.Unconfigured;

        var ex = Assert.Throws<InvalidOperationException>(() => _ = db.DataSource);
        Assert.Contains("ConnectionStrings:Wisper", ex.Message);
    }

    [Fact]
    public async Task Unconfigured_OpenConnection_throws()
    {
        var db = Db.Unconfigured;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.OpenConnectionAsync());
    }
}
