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

    /// <summary>Clicking a header: the SAME column flips direction; a NEW column starts ascending — the
    /// convention every sortable table on the web uses.</summary>
    public void Toggle(string column)
    {
        if (Column == column)
        {
            Descending = !Descending;
        }
        else
        {
            Column = column;
            Descending = false;
        }
    }

    /// <summary>Restore a persisted sort (from table-sort.js) without the toggle semantics.</summary>
    public void Set(string? column, bool descending)
    {
        Column = column;
        Descending = descending;
    }

    /// <summary>Order <paramref name="items"/> by the active column's <paramref name="key"/> (the page picks
    /// the key selector for <see cref="Column"/> — its rows and comparisons live there) in the active
    /// direction, with a STABLE ascending <paramref name="tiebreak"/> so equal rows keep a deterministic
    /// order and re-sorts don't shuffle. Returns the items untouched when no column is active (the table's
    /// natural order). One definition of "apply the sort", shared by every sortable table.</summary>
    public IEnumerable<T> Order<T>(
        IEnumerable<T> items, Func<T, IComparable?> key, Func<T, IComparable?> tiebreak)
    {
        if (Column is null) return items;
        var ordered = Descending ? items.OrderByDescending(key) : items.OrderBy(key);
        return ordered.ThenBy(tiebreak);
    }
}
