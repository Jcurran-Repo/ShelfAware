using ShelfAware.Web.Components;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The persisted-sort controller's restore guard: a saved column key is honored only if the table still has
/// a header for it, so a key renamed across a deploy — while a device holds the old preference — falls back
/// to natural order instead of sorting by a fallback with no header showing active.
/// </summary>
public class TableSortControllerTests
{
    private static readonly IReadOnlySet<string> Columns = new HashSet<string> { "name", "count" };

    [Fact]
    public void A_known_saved_column_is_restored() =>
        Assert.True(TableSortController.IsKnownColumn("count", Columns));

    [Fact]
    public void A_saved_column_the_table_no_longer_has_is_ignored() =>
        Assert.False(TableSortController.IsKnownColumn("renamed-away", Columns));

    [Fact]
    public void A_null_saved_column_is_ignored() =>
        Assert.False(TableSortController.IsKnownColumn(null, Columns));
}
