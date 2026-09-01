namespace ShelfAware.Web.Components;

/// <summary>
/// The chosen sort of a data table: which column, ascending or descending. Shared by every sortable table
/// (with <see cref="SortableHeader"/> for the clickable headers and <c>js/table-sort.js</c> for per-device
/// persistence), so the click behaviour and the persisted shape are defined once. Pure — a page applies it
/// to its own rows with its own per-column comparison.
/// </summary>
public sealed class TableSort(string? column = null, bool descending = false)
{
    /// <summary>The active sort column's key, or null for the table's natural (unsorted) order.</summary>
    public string? Column { get; private set; } = column;

    public bool Descending { get; private set; } = descending;

    /// <summary>Clicking a header cycles a NEW column → ascending → descending → OFF (the table's natural
    /// order). The third click "un-sorts" back to how the table came — receipt order on the review grid, the
    /// name/load order elsewhere — so there's always a way back, not just a flip.</summary>
    public void Toggle(string column)
    {
        if (Column != column) { Column = column; Descending = false; } // new column → ascending
        else if (!Descending) { Descending = true; }                  // ascending → descending
        else { Column = null; Descending = false; }                   // descending → off (natural order)
    }

    /// <summary>Restore a persisted sort (from table-sort.js) without the toggle semantics.</summary>
    public void Set(string? column, bool descending)
    {
        Column = column;
        Descending = descending;
    }

    /// <summary>Order <paramref name="items"/> by the active column's <paramref name="key"/> (the page picks
    /// the key selector for <see cref="Column"/> — its rows and comparisons live there) in the active
    /// direction. Returns the items UNTOUCHED when no column is active (the table's natural order — receipt
    /// order on the review grid, name/load order elsewhere). An optional ascending <paramref name="tiebreak"/>
    /// gives equal rows a deterministic order; omit it to keep the input's own order for ties (OrderBy is
    /// stable), which is what a table whose natural order IS meaningful wants. One shared definition.</summary>
    public IEnumerable<T> Order<T>(
        IEnumerable<T> items, Func<T, IComparable?> key, Func<T, IComparable?>? tiebreak = null)
    {
        if (Column is null) return items;
        var ordered = Descending ? items.OrderByDescending(key) : items.OrderBy(key);
        return tiebreak is null ? ordered : ordered.ThenBy(tiebreak);
    }
}
