using ShelfAware.Web.Components;
using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.UI.Tests;

/// <summary>The admin's read-only view of a snapshot a reporter attached (BugReport.StateJson). It parses
/// the stored blob forgivingly and shows only the sections that are present — a pre-feature or corrupt row
/// renders nothing rather than throwing on the admin's screen.</summary>
public class BugSnapshotViewTests : PageTestContext
{
    private static string Both() => new BugReportSnapshot(
        new BugDiagnostics("/product/12", "800x600 @2x", "TestUA", "dark (auto)", ReducedMotion: true,
            "Aug 25, 7:02 PM", "America/New_York", ["TypeError: boom @ x.js:1"]),
        "Milk\nEggs").Serialize();

    [Fact]
    public void Renders_the_attached_diagnostics_and_page_content()
    {
        var cut = Render<BugSnapshotView>(ps => ps.Add(p => p.Json, Both()));

        Assert.Contains("Attached details", cut.Markup);
        Assert.Contains("/product/12", cut.Markup);
        Assert.Contains("dark (auto)", cut.Markup);
        Assert.Contains("reduced motion", cut.Markup);
        Assert.Contains("TypeError: boom", cut.Markup);
        Assert.Contains("What was on the page", cut.Markup);
        Assert.Contains("Milk", cut.Markup);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    public void Renders_nothing_when_there_is_no_valid_snapshot(string? json)
    {
        var cut = Render<BugSnapshotView>(ps => ps.Add(p => p.Json, json));
        Assert.Equal("", cut.Markup.Trim());
    }

    [Fact]
    public void A_diagnostics_only_snapshot_shows_no_page_content_section()
    {
        var json = new BugReportSnapshot(
            new BugDiagnostics("/list", null, null, "light", false, null, null, null), null).Serialize();
        var cut = Render<BugSnapshotView>(ps => ps.Add(p => p.Json, json));

        Assert.Contains("/list", cut.Markup);
        Assert.DoesNotContain("What was on the page", cut.Markup);
    }

    [Fact]
    public void A_page_content_only_snapshot_shows_no_diagnostics()
    {
        var json = new BugReportSnapshot(null, "Milk").Serialize();
        var cut = Render<BugSnapshotView>(ps => ps.Add(p => p.Json, json));

        Assert.Contains("What was on the page", cut.Markup);
        Assert.Contains("Milk", cut.Markup);
        Assert.DoesNotContain("Browser", cut.Markup); // the diagnostics dl isn't there
    }
}
