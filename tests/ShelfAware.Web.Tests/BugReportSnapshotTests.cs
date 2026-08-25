using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.Tests;

public class BugReportSnapshotTests
{
    private static BugDiagnostics SampleDiagnostics() => new(
        Url: "/product/12?tab=history",
        Viewport: "390x844 @3x",
        UserAgent: "iPhone; Safari 17",
        Theme: "dark",
        ReducedMotion: false,
        LocalTime: "Aug 25, 2026, 7:02 PM",
        TimeZone: "America/New_York",
        JsErrors: new[] { "TypeError: x is null @ readaloud.js:88" });

    [Fact]
    public void Serialize_then_parse_preserves_both_sections()
    {
        var snapshot = new BugReportSnapshot(SampleDiagnostics(), "Milk\nEggs");

        var parsed = BugReportSnapshot.TryParse(snapshot.Serialize());

        Assert.NotNull(parsed);
        Assert.Equal("Milk\nEggs", parsed!.PageContent);
        Assert.NotNull(parsed.Diagnostics);
        Assert.Equal("/product/12?tab=history", parsed.Diagnostics!.Url);
        Assert.Equal("dark", parsed.Diagnostics.Theme);
        Assert.False(parsed.Diagnostics.ReducedMotion);
        Assert.Equal("America/New_York", parsed.Diagnostics.TimeZone);
        Assert.Equal(new[] { "TypeError: x is null @ readaloud.js:88" }, parsed.Diagnostics.JsErrors);
    }

    [Fact]
    public void HasAnything_is_false_only_when_both_sections_are_absent()
    {
        Assert.False(new BugReportSnapshot(null, null).HasAnything);
        Assert.True(new BugReportSnapshot(SampleDiagnostics(), null).HasAnything);
        Assert.True(new BugReportSnapshot(null, "Milk").HasAnything);
        Assert.True(new BugReportSnapshot(SampleDiagnostics(), "Milk").HasAnything);
    }

    [Fact]
    public void A_snapshot_with_one_section_removed_serializes_and_parses_that_way()
    {
        // The store path: the reporter dropped page content, kept diagnostics.
        var kept = new BugReportSnapshot(SampleDiagnostics(), null);

        var parsed = BugReportSnapshot.TryParse(kept.Serialize());

        Assert.NotNull(parsed);
        Assert.Null(parsed!.PageContent);
        Assert.NotNull(parsed.Diagnostics);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ broken")]
    public void TryParse_yields_null_for_absent_or_garbage_blobs(string? json)
    {
        // A report predating this feature, or a corrupt row, must degrade to "nothing attached" rather
        // than throw where it's rendered on the admin's screen.
        Assert.Null(BugReportSnapshot.TryParse(json));
    }

    [Fact]
    public void Bounded_clamps_oversized_content_fields_and_errors()
    {
        // The JS caps are browser-enforced only; a tampered client can return an arbitrarily large snapshot
        // over the circuit. Bounded() is the server-side clamp before storage.
        var huge = new BugReportSnapshot(
            new BugDiagnostics(
                Url: new string('u', 2000),
                Viewport: "800x600",
                UserAgent: new string('a', 2000),
                Theme: "dark",
                ReducedMotion: false,
                LocalTime: "now",
                TimeZone: "tz",
                JsErrors: Enumerable.Range(0, 50).Select(_ => new string('e', 1000)).ToList()),
            new string('p', 20_000));

        var bounded = huge.Bounded();

        Assert.True(bounded.PageContent!.Length <= 10_000);
        Assert.True(bounded.Diagnostics!.Url!.Length <= 512);
        Assert.True(bounded.Diagnostics.UserAgent!.Length <= 512);
        Assert.True(bounded.Diagnostics.JsErrors!.Count <= 30);
        Assert.All(bounded.Diagnostics.JsErrors, e => Assert.True(e.Length <= 512));
        // A within-bounds field is left untouched.
        Assert.Equal("dark", bounded.Diagnostics.Theme);
    }

    [Fact]
    public void Bounded_leaves_a_within_bounds_snapshot_unchanged()
    {
        var snap = new BugReportSnapshot(SampleDiagnostics(), "Milk\nEggs");

        var bounded = snap.Bounded();

        Assert.Equal("Milk\nEggs", bounded.PageContent);
        Assert.Equal("/product/12?tab=history", bounded.Diagnostics!.Url);
        Assert.Equal(new[] { "TypeError: x is null @ readaloud.js:88" }, bounded.Diagnostics.JsErrors);
    }

    [Fact]
    public void TryParse_tolerates_a_partial_legacy_blob()
    {
        // Only page content, no diagnostics object at all — a shape a future/older writer might produce.
        var parsed = BugReportSnapshot.TryParse("""{"pageContent":"Milk"}""");

        Assert.NotNull(parsed);
        Assert.Equal("Milk", parsed!.PageContent);
        Assert.Null(parsed.Diagnostics);
    }
}
