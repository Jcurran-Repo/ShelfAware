using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>The /history page: a household's actions grouped by day, each with an Undo button only
/// when reversing would truly act. The page runs the SAME precondition (PeekAsync) the real undo does,
/// so a no-op undo (its target gone, or superseded) shows greyed rather than as a dead button.</summary>
public class HistoryPageTests : PageTestContext
{
    protected override void RegisterAdditionalServices()
    {
        var auth = this.AddAuthorization();
        auth.SetAuthorized("wife@test.local");
    }

    private IRenderedComponent<History> RenderHistory()
    {
        var cut = Render<History>();
        cut.WaitForState(() => !cut.Markup.Contains("Loading…"));
        return cut;
    }

    private static AngleSharp.Dom.IElement RowFor(IRenderedComponent<History> cut, string summary) =>
        cut.FindAll("tbody tr").Single(r => r.TextContent.Contains(summary));

    private async Task<bool> ProductExists(int id)
    {
        await using var db = Db.CreateDbContext();
        return await db.Products.AnyAsync(p => p.Id == id);
    }

    [Fact]
    public async Task Actions_render_grouped_by_day_with_their_summaries()
    {
        var id = await Store.CreateProductAsync("Olive Oil", Category.Pantry, []);
        await Store.AddPurchaseAsync(id, DateOnly.FromDateTime(DateTime.Today), 2, PurchaseSource.Manual);

        var cut = RenderHistory();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Today", cut.Find("caption").TextContent);
            Assert.Contains("Added Olive Oil", cut.Markup);
            Assert.Contains("Bought 2 × Olive Oil", cut.Markup);
        });
    }

    [Fact]
    public async Task An_undoable_action_can_be_undone_from_the_page()
    {
        var id = await Store.CreateProductAsync("Olive Oil", Category.Pantry, []);
        var cut = RenderHistory();
        cut.WaitForAssertion(() => Assert.Contains("Added Olive Oil", cut.Markup));

        RowFor(cut, "Added Olive Oil").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Undone: Added Olive Oil", cut.Markup); // announced
            var row = RowFor(cut, "Added Olive Oil");
            Assert.Contains("undone", row.TextContent);            // now shown as undone
            Assert.Empty(row.QuerySelectorAll("button"));          // and no longer offers a button
        });
        Assert.False(await ProductExists(id)); // really reversed
    }

    [Fact]
    public void A_history_only_action_shows_greyed_with_no_undo()
    {
        SeedEntry(ActivityKind.ProductsMerged, "Merged Butter into Butter", Reversibility.NotReversible);

        var cut = RenderHistory();

        var row = RowFor(cut, "Merged Butter into Butter");
        Assert.Contains("can't be undone", row.TextContent);
        Assert.Empty(row.QuerySelectorAll("button"));
        Assert.Contains("muted", row.ClassName);
    }

    [Fact]
    public async Task A_no_op_undo_is_greyed_not_offered()
    {
        // Jordan's call: if undoing would do nothing (its target gone), the page must not offer it.
        var id = await Store.CreateProductAsync("Olive Oil", Category.Pantry, []);
        await Store.AddPurchaseAsync(id, DateOnly.FromDateTime(DateTime.Today), 1, PurchaseSource.Manual);
        await DeletePurchases(id); // the purchase this entry would delete is already gone

        var cut = RenderHistory();

        var row = RowFor(cut, "Bought 1 × Olive Oil");
        Assert.Contains("no longer here", row.TextContent);
        Assert.Empty(row.QuerySelectorAll("button")); // no dead Undo button
        Assert.Contains("muted", row.ClassName);
    }

    [Fact]
    public void Show_all_reveals_older_entries_beyond_the_recent_window()
    {
        for (var i = 0; i < 35; i++)
            SeedEntry(ActivityKind.ProductsMerged, $"Older action {i}", Reversibility.NotReversible);

        var cut = RenderHistory();
        Assert.Equal(30, cut.FindAll("tbody tr").Count); // recent window
        Assert.Contains("Show all history", cut.Markup);

        cut.Find("button.secondary").Click();

        cut.WaitForAssertion(() => Assert.Equal(35, cut.FindAll("tbody tr").Count));
    }

    [Fact]
    public void A_failing_load_is_reported_instead_of_crashing_the_page()
    {
        Factory.FailAfter = 0;
        var cut = Render<History>();
        cut.WaitForAssertion(() => Assert.Contains("Couldn't load your history", cut.Markup));
    }

    private void SeedEntry(ActivityKind kind, string summary, Reversibility reversibility)
    {
        using var db = Db.CreateDbContext();
        db.ActivityEntries.Add(new ActivityEntry
        {
            Kind = kind, OccurredAt = DateTimeOffset.Now, Summary = summary,
            PayloadJson = "{}", Reversibility = reversibility,
        });
        db.SaveChanges();
    }

    private async Task DeletePurchases(int productId)
    {
        await using var db = Db.CreateDbContext();
        await db.PurchaseEvents.Where(p => p.ProductId == productId).ExecuteDeleteAsync();
    }
}
