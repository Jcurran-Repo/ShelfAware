using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Undo;

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
        SeedEntry(ActivityKind.CensusConfirmed, "Counted 3 items from a photo", Reversibility.NotReversible);

        var cut = RenderHistory();

        var row = RowFor(cut, "Counted 3 items from a photo");
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
    public async Task A_built_on_recipe_undo_warns_and_only_deletes_on_confirm()
    {
        // A saved recipe that's been cooked → the undo must WARN (not grey, not silently delete).
        var recipeId = await SeedRecipe("Sheet-Pan Chicken", timesEaten: 3);
        SeedRecipeSavedEntry(recipeId, "Sheet-Pan Chicken");
        var cut = RenderHistory();
        cut.WaitForAssertion(() => Assert.Contains("Saved recipe: Sheet-Pan Chicken", cut.Markup));

        // Offered (not greyed), and clicking pops the confirm dialog naming the history — nothing deleted yet.
        var row = RowFor(cut, "Saved recipe: Sheet-Pan Chicken");
        Assert.DoesNotContain("muted", row.ClassName);
        row.QuerySelector("button")!.Click();
        cut.WaitForAssertion(() => Assert.Contains("cooked it 3×", cut.Find("[role=alertdialog]").TextContent));
        Assert.True(await RecipeExists(recipeId));

        cut.FindAll("[role=alertdialog] button").Single(b => b.TextContent.Contains("Delete anyway")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Undone: Saved recipe: Sheet-Pan Chicken", cut.Markup));
        Assert.False(await RecipeExists(recipeId)); // deleted only after confirming
    }

    [Fact]
    public async Task Cancelling_the_warning_leaves_the_recipe()
    {
        var recipeId = await SeedRecipe("Sheet-Pan Chicken", timesEaten: 3);
        SeedRecipeSavedEntry(recipeId, "Sheet-Pan Chicken");
        var cut = RenderHistory();
        cut.WaitForAssertion(() => Assert.Contains("Saved recipe: Sheet-Pan Chicken", cut.Markup));

        RowFor(cut, "Saved recipe: Sheet-Pan Chicken").QuerySelector("button")!.Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=alertdialog]")));

        cut.FindAll("[role=alertdialog] button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=alertdialog]"))); // dialog closed
        Assert.True(await RecipeExists(recipeId));                                   // recipe survives
    }

    [Fact]
    public void Show_all_reveals_older_entries_beyond_the_recent_window()
    {
        for (var i = 0; i < 35; i++)
            SeedEntry(ActivityKind.CensusConfirmed, $"Older action {i}", Reversibility.NotReversible);

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

    [Fact]
    public async Task A_reload_failure_after_a_committed_undo_still_reverses_and_reports_the_load_error()
    {
        // The undo commits (one context); the reload right after it fails (FailAfter=1). LoadAsync is
        // self-catching, so it reports its own accurate loadError rather than bubbling a misleading
        // "couldn't undo" up to UndoAsync — and the reversal really did commit. (Re-verifies gate finding
        // #1's premise: no false "try again" is possible past a committed undo.)
        var id = await Store.CreateProductAsync("Olive Oil", Category.Pantry, []);
        var cut = RenderHistory();
        cut.WaitForAssertion(() => Assert.Contains("Added Olive Oil", cut.Markup));

        Factory.FailAfter = 1;
        RowFor(cut, "Added Olive Oil").QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("Couldn't load your history", cut.Markup)); // the load error, accurate
        Assert.DoesNotContain("Couldn't undo", cut.Markup); // never the false "the undo failed — try again"
        Assert.False(await ProductExists(id));              // and the reversal really did commit
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

    private async Task<int> SeedRecipe(string name, int timesEaten = 0)
    {
        await using var db = Db.CreateDbContext();
        var recipe = new Recipe { Name = name, SavedAt = DateTimeOffset.Now, TimesEaten = timesEaten };
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return recipe.Id;
    }

    private void SeedRecipeSavedEntry(int recipeId, string name)
    {
        using var db = Db.CreateDbContext();
        ActivityLog.Record(db, ActivityKind.RecipeSaved, new RecipeSavedPayload(recipeId, name));
        db.SaveChanges();
    }

    private async Task<bool> RecipeExists(int id)
    {
        await using var db = Db.CreateDbContext();
        return await db.Recipes.AnyAsync(r => r.Id == id);
    }
}
