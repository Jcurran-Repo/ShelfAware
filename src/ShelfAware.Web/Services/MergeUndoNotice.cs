namespace ShelfAware.Web.Services;

/// <summary>Per-circuit hand-off for a just-completed merge's inline "↩ Undo" affordance. A merge on Product
/// Detail deletes the SOURCE and navigates to the TARGET, so the notice can't live in a component field (the
/// product switch resets every transient panel) — the merge stashes it here and the target page picks it up
/// ONCE on load. Scoped, one-shot: a refresh or a later navigation doesn't re-show it. Same one-shot
/// cross-navigation carrier as <see cref="BugReportContext"/> and the tour/voice coordinators.</summary>
public sealed class MergeUndoNotice
{
    /// <summary>The pending affordance: the activity entry to undo, the source's name (for the wording), and
    /// the product the merge landed on (so the target page can claim it and no other page flashes it).</summary>
    public sealed record Pending(int EntryId, string SourceName, int TargetId);

    private Pending? _pending;

    /// <summary>Stash the affordance for the target page to pick up after the merge navigates there.</summary>
    public void Set(int entryId, string sourceName, int targetId) => _pending = new(entryId, sourceName, targetId);

    /// <summary>Take the pending notice IF it is for <paramref name="targetId"/>; one-shot (cleared on read).
    /// The id guard is defensive — the very next page after a merge IS the target, but if the user navigated
    /// elsewhere first a stale notice must not flash on the wrong product.</summary>
    public Pending? TakeFor(int targetId)
    {
        if (_pending is { } p && p.TargetId == targetId)
        {
            _pending = null;
            return p;
        }
        return null;
    }
}
