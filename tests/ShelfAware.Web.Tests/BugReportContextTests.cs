using ShelfAware.Web.Diagnostics;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

public class BugReportContextTests
{
    [Fact]
    public void Take_returns_what_was_stashed_then_clears_it()
    {
        var ctx = new BugReportContext();
        Assert.Null(ctx.TakePending()); // nothing captured yet

        var snapshot = new BugReportSnapshot(null, "Milk");
        ctx.Stash(snapshot);

        Assert.Same(snapshot, ctx.TakePending());
        // Cleared on take, so a back-nav to /bugs (or a later direct visit) doesn't re-show a stale capture.
        Assert.Null(ctx.TakePending());
    }

    [Fact]
    public void Stashing_null_clears_a_previous_snapshot()
    {
        var ctx = new BugReportContext();
        ctx.Stash(new BugReportSnapshot(null, "Milk"));

        ctx.Stash(null);

        Assert.Null(ctx.TakePending());
    }
}
