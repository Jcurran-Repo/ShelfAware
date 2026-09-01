using ShelfAware.Web.Components;

namespace ShelfAware.Web.Tests;

/// <summary>The shared column-sort state: clicking a header sorts a new column ascending and flips the
/// active one; Order applies the active direction with a stable tiebreak, and passes items through untouched
/// when no column is chosen.</summary>
public class TableSortTests
{
    [Fact]
    public void A_new_column_starts_ascending()
    {
        var s = new TableSort();
        s.Toggle("name");
        Assert.Equal("name", s.Column);
        Assert.False(s.Descending);
    }

    [Fact]
    public void Clicking_the_same_column_flips_the_direction()
    {
        var s = new TableSort();
        s.Toggle("name");
        s.Toggle("name");
        Assert.True(s.Descending);
        s.Toggle("name");
        Assert.False(s.Descending);
    }

    [Fact]
    public void Switching_columns_resets_to_ascending()
    {
        var s = new TableSort();
        s.Toggle("name");
        s.Toggle("name"); // name, descending
        s.Toggle("category");
        Assert.Equal("category", s.Column);
        Assert.False(s.Descending); // a new column doesn't inherit the old direction
    }

    [Fact]
    public void Order_leaves_items_untouched_when_no_column_is_active()
    {
        var s = new TableSort();
        var items = new[] { "b", "a", "c" };
        Assert.Equal(items, s.Order(items, x => x, x => x));
    }

    [Fact]
    public void Order_sorts_ascending_then_descending()
    {
        var s = new TableSort("x");
        int[] items = [3, 1, 2];
        Assert.Equal([1, 2, 3], s.Order(items, x => x, x => x));
        s.Toggle("x"); // flip to descending
        Assert.Equal([3, 2, 1], s.Order(items, x => x, x => x));
    }

    [Fact]
    public void Order_breaks_ties_with_the_tiebreak_ascending()
    {
        var s = new TableSort("k");
        (string Name, int Key)[] items = [("b", 0), ("a", 0), ("c", 0)]; // all equal on the primary key
        var sorted = s.Order(items, x => x.Key, x => x.Name).Select(x => x.Name);
        Assert.Equal(["a", "b", "c"], sorted);
    }
}
