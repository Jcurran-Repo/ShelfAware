using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.Services;

/// <summary>Per-circuit hand-off for the diagnostic snapshot a reporter is about to attach. The footer's
/// "Report a bug" link captures the snapshot on the page the reporter is looking at — that page is gone by
/// the time /bugs renders (Blazor disposed its component on navigation), so the snapshot can't be read
/// there; it's captured at the click, stashed here, and /bugs collects it once. Scoped = one instance per
/// Blazor circuit (single user session), so it never crosses users, and it never touches the database — a
/// courier, not a store.</summary>
public sealed class BugReportContext
{
    private BugReportSnapshot? _pending;

    /// <summary>Stash the snapshot the footer just captured, for /bugs to collect. Null clears it.</summary>
    public void Stash(BugReportSnapshot? snapshot) => _pending = snapshot;

    /// <summary>Hand the captured snapshot to /bugs and clear it — so a later direct visit or a back-nav to
    /// /bugs doesn't re-show a stale capture from an earlier report. Returns null when nothing was captured
    /// (the reporter reached /bugs some other way, or capture failed).</summary>
    public BugReportSnapshot? TakePending()
    {
        var snapshot = _pending;
        _pending = null;
        return snapshot;
    }
}
