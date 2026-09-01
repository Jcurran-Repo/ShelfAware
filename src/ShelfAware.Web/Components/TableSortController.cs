using Microsoft.JSInterop;

namespace ShelfAware.Web.Components;

/// <summary>
/// A persisted table sort: the <see cref="TableSort"/> state plus its per-device localStorage persistence
/// (<c>js/table-sort.js</c>), owned in ONE place so the import / restore / save / dispose protocol isn't
/// copy-pasted per page. A page holds one per sortable table, calls <see cref="LoadAsync"/> once on first
/// render and <see cref="ToggleAsync"/> on a header click, and applies <see cref="Sort"/> to its own rows
/// via <see cref="TableSort.Order"/>. A table that deliberately doesn't persist (the receipt review grid)
/// uses a bare <see cref="TableSort"/> instead.
/// </summary>
public sealed class TableSortController : IAsyncDisposable
{
    private readonly string _id;
    private readonly HashSet<string> _columns;
    private IJSObjectReference? _module;

    /// <param name="id">The localStorage table id (e.g. "products").</param>
    /// <param name="columns">Every current sortable column key for this table — a restored key not in this
    /// set is ignored (see <see cref="LoadAsync"/>).</param>
    public TableSortController(string id, params string[] columns)
    {
        _id = id;
        _columns = new HashSet<string>(columns, StringComparer.Ordinal);
    }

    /// <summary>The sort state to apply to the page's rows and to hand each <see cref="SortableHeader"/>.</summary>
    public TableSort Sort { get; } = new();

    /// <summary>Import the persistence module and restore the saved sort — once, on first render. Returns
    /// true when a saved sort was actually restored (so the caller re-renders only then). A saved column
    /// that is no longer a known header key is IGNORED: otherwise, after a column key was renamed across a
    /// deploy while a device still held the old one, the table would sort by a fallback with NO header
    /// showing active and nothing to explain the order. Interop teardown races are swallowed.</summary>
    public async Task<bool> LoadAsync(IJSRuntime js)
    {
        try
        {
            _module = await js.InvokeAsync<IJSObjectReference>("import", "/js/table-sort.js");
            if (await _module.InvokeAsync<SavedSort?>("get", _id) is { } saved && IsKnownColumn(saved.Col, _columns))
            {
                Sort.Set(saved.Col, saved.Desc);
                return true;
            }
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        return false;
    }

    /// <summary>Cycle the sort on a header click and persist the new state (best-effort — a storage failure
    /// just means it isn't remembered; a teardown race is swallowed).</summary>
    public async Task ToggleAsync(string column)
    {
        Sort.Toggle(column);
        if (_module is null) return;
        try { await _module.InvokeVoidAsync("set", _id, Sort.Column, Sort.Descending); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Whether a restored column key is one this table still has a header for — the guard in
    /// <see cref="LoadAsync"/>, pulled out to be unit-testable without JS interop.</summary>
    internal static bool IsKnownColumn(string? col, IReadOnlySet<string> columns) =>
        col is not null && columns.Contains(col);

    // The persisted shape from js/table-sort.js get(): {col, desc}. Col is non-null here because get()
    // only returns a record when col is a string (a "sort off" save reads back as null → no restore).
    private sealed record SavedSort(string Col, bool Desc);
}
