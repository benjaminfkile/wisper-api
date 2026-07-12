using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Exercises the shared in-memory test-double base (<see cref="InMemoryRepositoryBase{TId,TEntity}"/>)
/// through a sample repository double, the way real entity doubles (users, hosts, …) will use it so
/// the unit suite can run without a live database. Semantics must mirror the SQL side — notably a
/// duplicate key is rejected like a primary-key constraint.
/// </summary>
public class InMemoryRepositoryBaseTests
{
    private sealed record Widget(Guid Id, string Name);

    /// <summary>A minimal double built on the shared base, standing in for a real entity repository.</summary>
    private sealed class WidgetRepository : InMemoryRepositoryBase<Guid, Widget>
    {
        protected override Guid KeyOf(Widget entity) => entity.Id;

        public void Add(Widget widget) => Insert(widget);
        public void Save(Widget widget) => Upsert(widget);
        public Widget? Get(Guid id) => Find(id);
        public Widget? GetByName(string name) => FindBy(w => w.Name == name);
        public IReadOnlyList<Widget> StartingWith(string prefix) =>
            Where(w => w.Name.StartsWith(prefix, StringComparison.Ordinal));
        public IReadOnlyList<Widget> ListAll() => All();
        public bool Delete(Guid id) => Remove(id);
        public int Total => Count;
    }

    [Fact]
    public void Insert_then_find_round_trips()
    {
        var repo = new WidgetRepository();
        var widget = new Widget(Guid.NewGuid(), "alpha");

        repo.Add(widget);

        Assert.Equal(widget, repo.Get(widget.Id));
        Assert.Equal(widget, repo.GetByName("alpha"));
        Assert.Equal(1, repo.Total);
    }

    [Fact]
    public void Insert_duplicate_key_throws_like_a_pk_constraint()
    {
        var repo = new WidgetRepository();
        var id = Guid.NewGuid();
        repo.Add(new Widget(id, "first"));

        Assert.Throws<InvalidOperationException>(() => repo.Add(new Widget(id, "second")));
    }

    [Fact]
    public void Upsert_replaces_existing_entity()
    {
        var repo = new WidgetRepository();
        var id = Guid.NewGuid();
        repo.Add(new Widget(id, "old"));

        repo.Save(new Widget(id, "new"));

        Assert.Equal("new", repo.Get(id)!.Name);
        Assert.Equal(1, repo.Total);
    }

    [Fact]
    public void Where_and_all_filter_and_snapshot()
    {
        var repo = new WidgetRepository();
        repo.Add(new Widget(Guid.NewGuid(), "ax"));
        repo.Add(new Widget(Guid.NewGuid(), "ay"));
        repo.Add(new Widget(Guid.NewGuid(), "bz"));

        Assert.Equal(2, repo.StartingWith("a").Count);
        Assert.Equal(3, repo.ListAll().Count);
    }

    [Fact]
    public void Remove_deletes_and_reports()
    {
        var repo = new WidgetRepository();
        var id = Guid.NewGuid();
        repo.Add(new Widget(id, "gone"));

        Assert.True(repo.Delete(id));
        Assert.False(repo.Delete(id));
        Assert.Null(repo.Get(id));
        Assert.Equal(0, repo.Total);
    }

    [Fact]
    public void Missing_lookups_return_null()
    {
        var repo = new WidgetRepository();

        Assert.Null(repo.Get(Guid.NewGuid()));
        Assert.Null(repo.GetByName("nope"));
    }
}
