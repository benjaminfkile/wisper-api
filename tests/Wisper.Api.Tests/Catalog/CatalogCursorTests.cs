using Wisper.Api.Catalog;
using Xunit;

namespace Wisper.Api.Tests.Catalog;

/// <summary>
/// Unit tests for <see cref="CatalogCursor"/> (docs/API.md §10): the opaque token round-trips its
/// <c>(created_at, id)</c> key, malformed tokens are rejected, and the page comparator sorts by the
/// stable descending key.
/// </summary>
public class CatalogCursorTests
{
    [Fact]
    public void Round_trips_through_encode_and_parse()
    {
        var cursor = new CatalogCursor(new DateTimeOffset(2026, 7, 12, 3, 4, 5, TimeSpan.Zero), Guid.NewGuid());

        Assert.True(CatalogCursor.TryParse(cursor.Encode(), out var parsed));
        Assert.Equal(cursor, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("!!!not-base64!!!")]
    [InlineData("bm8tY29sb24")] // base64 of "no-colon"
    public void Rejects_malformed_tokens(string? raw)
    {
        Assert.False(CatalogCursor.TryParse(raw, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Compare_orders_newer_created_at_first()
    {
        var older = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = older.AddDays(1);
        var id = Guid.NewGuid();

        Assert.True(CatalogCursor.Compare(newer, id, older, id) < 0);
        Assert.True(CatalogCursor.Compare(older, id, newer, id) > 0);
    }

    [Fact]
    public void Compare_breaks_ties_by_larger_id_first()
    {
        var t = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var small = new Guid("00000000-0000-0000-0000-000000000001");
        var large = new Guid("00000000-0000-0000-0000-000000000002");

        Assert.True(CatalogCursor.Compare(t, large, t, small) < 0);
        Assert.Equal(0, CatalogCursor.Compare(t, small, t, small));
    }
}
