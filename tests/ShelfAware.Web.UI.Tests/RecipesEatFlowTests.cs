using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The Recipes page's "Ate it" flow — §13.3's tell-don't-ask contract driven through the rendered
/// markup. The rules under test: one tap commits everything (counter, dated MealEvent, one package
/// off each counted main) and the notice then SAYS what happened, with Undo as the one-tap way back;
/// the failure advice splits on WHICH context died, because "didn't save — try again" after a landed
/// write invites a second tap on a non-idempotent act; a main the app can't attribute to one shelf is
/// ASKED about via the picker (never guessed, even with a single candidate when the grounded product
/// exists uncounted); click-away answers "none of these" and moves no count; and the picker's close
/// paths are gated so a backdrop click or Dismiss cannot interleave with an in-flight pick — filing
/// an answered question as skipped, or orphaning a saved decrement with no notice and no Undo.
/// </summary>
public class RecipesEatFlowTests : PageTestContext
{

    /// <summary>Midnight <paramref name="daysAgo"/> days back, offset zero — a fixed instant chosen
    /// by the test, never derived mid-assertion.</summary>
    private static DateTimeOffset Clock(int daysAgo) =>
        new(Today.AddDays(-daysAgo).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>A product the household counts (or deliberately doesn't). Purchase quantities drive
    /// TypicalPackage: whole-number buys make "one" exactly 1; no buys at all also read as 1 (the
    /// census-stock shape — counted, never bought through the app).</summary>
    private int SeedProduct(string name, bool counted, decimal? onHand, string? unit = null, params decimal[] buys)
    {
        using var db = Db.CreateDbContext();
        var product = new Product
        {
            Name = name,
            Category = Category.Meat,
            DefaultUnit = unit,
            TrackQuantity = counted,
            QuantityOnHand = onHand,
            QuantityCountedAt = counted ? Clock(3) : null,
            Purchases = [.. buys.Select((q, i) =>
                new PurchaseEvent { PurchasedAt = Today.AddDays(-30 + i * 7), Quantity = q })],
        };
        db.Products.Add(product);
        db.SaveChanges();
        return product.Id;
    }

    /// <summary>A saved recipe with one MAIN ingredient — the only kind that may move a count.</summary>
    private int SeedRecipe(string name, string mainName, string? matchedProduct)
    {
        using var db = Db.CreateDbContext();
        var recipe = new Recipe
        {
            Name = name,
            SavedAt = DateTimeOffset.Now,
            Ingredients = [new RecipeIngredient { Name = mainName, IsMain = true, MatchedProduct = matchedProduct }],
        };
        db.Recipes.Add(recipe);
        db.SaveChanges();
        return recipe.Id;
    }

    private IRenderedComponent<Recipes> RenderRecipes()
    {
        var cut = Render<Recipes>();
        cut.WaitForState(() => cut.FindAll(".saved-recipes li").Count > 0);
        return cut;
    }

    private static void ClickAteIt(IRenderedComponent<Recipes> cut, string recipeName) =>
        cut.Find($"button[aria-label='Mark {recipeName} eaten']").Click();


    // ---------------------------------------------------------------- one tap commits, notice tells

    [Fact]
    public async Task One_tap_commits_the_meal_and_the_notice_reports_the_exact_take()
    {
        // Grounded to the one counted product: nothing to ask, so the single tap must do it all.
        var productId = SeedProduct("Beef Chuck Roast", counted: true, onHand: 3m, unit: "cans", 1m, 1m);
        var recipeId = SeedRecipe("Beef Stew", "beef chuck roast", "Beef Chuck Roast");
        var cut = RenderRecipes();

        ClickAteIt(cut, "Beef Stew");

        cut.WaitForAssertion(() =>
        {
            // Tell, don't ask: the notice IS the interaction — no dialog stands between tap and commit.
            Assert.Empty(cut.FindAll("[role=dialog]"));
            var notice = cut.Find(".inline-confirm");
            Assert.Contains("Marked eaten — and taken off what you have on hand:", notice.TextContent);
            // The take line routes through QuantityFormat (unit attached, exactly-1 singularized) —
            // the notice must state the ACTUAL movement, in the same words every other surface uses.
            Assert.Equal("Beef Chuck Roast — 1 can off, 2 cans left", Collapsed(cut.Find(".inline-confirm li")));
        });

        // The same tap wrote all three facts, or the notice is narrating a commit that didn't happen.
        await using var raw = Db.CreateUnscopedContext();
        var meal = Assert.Single(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(recipeId, meal.RecipeId);
        Assert.Equal(Today, meal.AteAt);
        Assert.Equal(1, (await raw.Recipes.IgnoreQueryFilters().SingleAsync(r => r.Id == recipeId)).TimesEaten);
        Assert.Equal(2m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == productId)).QuantityOnHand);
    }

    [Fact]
    public async Task Undo_reverses_the_meal_exactly()
    {
        var productId = SeedProduct("Beef Chuck Roast", counted: true, onHand: 3m, unit: "cans", 1m, 1m);
        var recipeId = SeedRecipe("Beef Stew", "beef chuck roast", "Beef Chuck Roast");
        var cut = RenderRecipes();
        ClickAteIt(cut, "Beef Stew");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".inline-confirm")));

        cut.FindAll(".inline-confirm button").Single(b => b.TextContent.Contains("Undo")).Click();

        // The notice leaves with the meal it reported — a lingering panel would offer a second undo
        // of a reversal already made.
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".inline-confirm")));

        // All three writes step back: event removed, counter down, the ACTUAL taken amount returned.
        await using var raw = Db.CreateUnscopedContext();
        Assert.Empty(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0, (await raw.Recipes.IgnoreQueryFilters().SingleAsync(r => r.Id == recipeId)).TimesEaten);
        Assert.Equal(3m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == productId)).QuantityOnHand);
    }

    // ------------------------------------------------------------ the meal is an undoable log entry

    [Fact]
    public async Task Ate_it_records_a_MealLogged_entry_committed_with_the_meal()
    {
        SeedProduct("Beef Chuck Roast", counted: true, onHand: 3m, unit: "cans", 1m, 1m);
        SeedRecipe("Beef Stew", "beef chuck roast", "Beef Chuck Roast");
        var cut = RenderRecipes();

        ClickAteIt(cut, "Beef Stew");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".inline-confirm")));

        // The tap wrote the meal AND its undo record, atomically — one entry, one meal, committed together.
        await using var raw = Db.CreateUnscopedContext();
        var entry = Assert.Single(await raw.ActivityEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(ActivityKind.MealLogged, entry.Kind);
        Assert.Equal("Logged a meal: Beef Stew", entry.Summary);
        Assert.Null(entry.UndoneAt);
        Assert.Single(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Inline_undo_reverses_through_the_activity_log_and_stamps_the_entry()
    {
        SeedProduct("Beef Chuck Roast", counted: true, onHand: 3m, unit: "cans", 1m, 1m);
        SeedRecipe("Beef Stew", "beef chuck roast", "Beef Chuck Roast");
        var cut = RenderRecipes();
        ClickAteIt(cut, "Beef Stew");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".inline-confirm")));

        cut.FindAll(".inline-confirm button").Single(b => b.TextContent.Contains("Undo")).Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".inline-confirm")));

        // The inline Undo runs the SAME undo path /history does, so the entry is stamped undone — /history
        // shows the meal reversed, never as a live undo of a meal already gone.
        await using var raw = Db.CreateUnscopedContext();
        var entry = Assert.Single(await raw.ActivityEntries.IgnoreQueryFilters().ToListAsync());
        Assert.NotNull(entry.UndoneAt);
        Assert.Empty(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Undo_after_a_pick_restores_the_picked_package_too()
    {
        // The recipe pins to an UNCOUNTED store pack, so the only take is the picker's — no auto-take at
        // all. The pick restates the meal's undo record; the inline Undo then puts that package back. If
        // the restate were a no-op the payload would stay empty and the undo would restore nothing, leaving
        // the freezer wrongly short — so this pins the durable record tracking a staged pick, end to end.
        SeedProduct("Ground Beef", counted: false, onHand: null, unit: null, 1m, 1m);
        var freezerId = SeedProduct("Quarter Cow Ground Beef", counted: true, onHand: 14m);
        SeedRecipe("Freezer Chili", "ground beef", "Ground Beef");
        var cut = RenderRecipes();
        ClickAteIt(cut, "Freezer Chili");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".picker-modal .alt-bubble")));

        cut.FindAll(".picker-modal .alt-bubble")[0].Click(); // pick the freezer stock
        cut.WaitForAssertion(() => Assert.Contains("1 off", Collapsed(cut.Find(".inline-confirm li"))));
        await using (var mid = Db.CreateUnscopedContext())
            Assert.Equal(13m, (await mid.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == freezerId)).QuantityOnHand);

        cut.FindAll(".inline-confirm button").Single(b => b.TextContent.Contains("Undo")).Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".inline-confirm")));

        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(14m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == freezerId)).QuantityOnHand);
        Assert.NotNull((await raw.ActivityEntries.IgnoreQueryFilters().SingleAsync()).UndoneAt);
        Assert.Empty(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
    }

    // ------------------------------------------------------- split failure advice, proven by DB state

    [Fact]
    public async Task A_failed_write_advises_retry_and_the_database_shows_nothing_landed()
    {
        var productId = SeedProduct("Beef Chuck Roast", counted: true, onHand: 3m, unit: "cans", 1m, 1m);
        var recipeId = SeedRecipe("Beef Stew", "beef chuck roast", "Beef Chuck Roast");
        var cut = RenderRecipes();

        Factory.FailAfter = 0; // the tap's own context dies — nothing can have been written
        ClickAteIt(cut, "Beef Stew");

        cut.WaitForAssertion(() => Assert.Equal(
            "That didn't save. Try again, or reload the page.",
            Collapsed(cut.Find(".error[role=alert]"))));
        // No notice: there is no commit to tell about, and rendering one would contradict the advice.
        Assert.Empty(cut.FindAll(".inline-confirm"));

        // "Try again" is only safe advice because the write provably never landed.
        await using var raw = Db.CreateUnscopedContext();
        Assert.Empty(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0, (await raw.Recipes.IgnoreQueryFilters().SingleAsync(r => r.Id == recipeId)).TimesEaten);
        Assert.Equal(3m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == productId)).QuantityOnHand);
    }

    [Fact]
    public async Task A_failed_reload_after_a_landed_write_advises_against_a_second_tap()
    {
        var productId = SeedProduct("Beef Chuck Roast", counted: true, onHand: 3m, unit: "cans", 1m, 1m);
        var recipeId = SeedRecipe("Beef Stew", "beef chuck roast", "Beef Chuck Roast");
        var cut = RenderRecipes();

        Factory.FailAfter = 1; // the tap's context succeeds; the reload's context dies
        ClickAteIt(cut, "Beef Stew");

        // Opposite advice from the failed write, because the DB state is opposite: a second tap here
        // would record a second meal and take a second package.
        cut.WaitForAssertion(() => Assert.Equal(
            "Recorded — but the page couldn't refresh. Reload to see it; don't tap again.",
            Collapsed(cut.Find(".error[role=alert]"))));

        await using var raw = Db.CreateUnscopedContext();
        Assert.Single(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(1, (await raw.Recipes.IgnoreQueryFilters().SingleAsync(r => r.Id == recipeId)).TimesEaten);
        Assert.Equal(2m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == productId)).QuantityOnHand);
    }

    // ------------------------------------------------------------------------------------ the picker

    [Fact]
    public async Task An_ambiguous_main_asks_with_live_counts_and_moves_nothing_until_answered()
    {
        // Two counted products both cover "ground beef" — taking a package off each would be wrong,
        // picking one silently would be arbitrary, so the page must ask.
        var chuckId = SeedProduct("Ground Beef Chuck", counted: true, onHand: 4m, unit: null, 1m, 1m);
        var sirloinId = SeedProduct("Ground Beef Sirloin", counted: true, onHand: 6m, unit: null, 1m, 1m);
        SeedRecipe("Weeknight Tacos", "ground beef", null);
        var cut = RenderRecipes();

        ClickAteIt(cut, "Weeknight Tacos");

        cut.WaitForAssertion(() =>
        {
            var modal = cut.Find("[role=dialog]");
            Assert.Equal("Which did you use?", modal.GetAttribute("aria-label"));
            // One bubble per candidate, each carrying its live count — the human tells same-name or
            // same-family products apart by the number.
            var bubbles = cut.FindAll(".picker-modal .alt-bubble");
            Assert.Equal(2, bubbles.Count);
            Assert.Equal("Ground Beef Chuck — 4 on hand", Collapsed(bubbles[0]));
            Assert.Equal("Ground Beef Sirloin — 6 on hand", Collapsed(bubbles[1]));
        });

        // The meal itself is committed (tell-don't-ask covers the eaten mark) — only the DECREMENT
        // waits for the answer.
        await using var raw = Db.CreateUnscopedContext();
        Assert.Single(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(4m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == chuckId)).QuantityOnHand);
        Assert.Equal(6m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == sirloinId)).QuantityOnHand);
    }

    [Fact]
    public async Task Picking_a_candidate_moves_only_that_count_and_retires_the_question()
    {
        var chuckId = SeedProduct("Ground Beef Chuck", counted: true, onHand: 4m, unit: null, 1m, 1m);
        var sirloinId = SeedProduct("Ground Beef Sirloin", counted: true, onHand: 6m, unit: null, 1m, 1m);
        SeedRecipe("Weeknight Tacos", "ground beef", null);
        var cut = RenderRecipes();
        ClickAteIt(cut, "Weeknight Tacos");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".picker-modal .alt-bubble").Count));

        cut.FindAll(".picker-modal .alt-bubble")[0].Click(); // Ground Beef Chuck

        cut.WaitForAssertion(() =>
        {
            // The answered question leaves the modal (nothing left to ask closes it entirely)…
            Assert.Empty(cut.FindAll("[role=dialog]"));
            // …and the pick lands under Taken, in the same report an automatic take would produce.
            Assert.Equal("Ground Beef Chuck — 1 off, 3 left", Collapsed(cut.Find(".inline-confirm li")));
        });

        // Exactly the chosen shelf moved — the other candidate is untouched.
        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(3m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == chuckId)).QuantityOnHand);
        Assert.Equal(6m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == sirloinId)).QuantityOnHand);
    }

    [Fact]
    public async Task A_grounded_link_to_an_uncounted_product_asks_even_with_one_candidate()
    {
        // The recipe is pinned to the store pack ("Ground Beef" — exists, uncounted). Token matching
        // still reaches the counted freezer stock, but decrementing it automatically would be the app
        // guessing WHICH ground beef got cooked — so the picker opens even though only one candidate
        // could answer.
        SeedProduct("Ground Beef", counted: false, onHand: null, unit: null, 1m, 1m);
        var freezerId = SeedProduct("Quarter Cow Ground Beef", counted: true, onHand: 14m);
        SeedRecipe("Freezer Chili", "ground beef", "Ground Beef");
        var cut = RenderRecipes();

        ClickAteIt(cut, "Freezer Chili");

        cut.WaitForAssertion(() =>
        {
            Assert.NotNull(cut.Find("[role=dialog]"));
            var bubble = Assert.Single(cut.FindAll(".picker-modal .alt-bubble"));
            Assert.Equal("Quarter Cow Ground Beef — 14 on hand", Collapsed(bubble));
        });

        // No answer yet, no movement: the freezer count stands until the human points at it.
        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(14m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == freezerId)).QuantityOnHand);
    }

    [Fact]
    public async Task Click_away_files_open_questions_as_skipped_and_moves_no_count()
    {
        var chuckId = SeedProduct("Ground Beef Chuck", counted: true, onHand: 4m, unit: null, 1m, 1m);
        var sirloinId = SeedProduct("Ground Beef Sirloin", counted: true, onHand: 6m, unit: null, 1m, 1m);
        SeedRecipe("Weeknight Tacos", "ground beef", null);
        var cut = RenderRecipes();
        ClickAteIt(cut, "Weeknight Tacos");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".picker-backdrop")));

        cut.Find(".picker-backdrop").Click();

        cut.WaitForAssertion(() =>
        {
            // "None of these" said with a click-away: the question is answered, not abandoned…
            Assert.Empty(cut.FindAll("[role=dialog]"));
            // …and SAID in the notice — a silent skip would leave a count quietly un-maintained.
            Assert.Contains("Left uncounted — you didn't pick a product for: ground beef",
                Collapsed(cut.Find(".inline-confirm")));
        });

        // The eaten mark stands; no count moved anywhere.
        await using var raw = Db.CreateUnscopedContext();
        Assert.Single(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(4m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == chuckId)).QuantityOnHand);
        Assert.Equal(6m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == sirloinId)).QuantityOnHand);
    }

    // ------------------------------------------------------------- the gated exits (interleaving)

    [Fact]
    public async Task A_backdrop_click_mid_pick_cannot_file_the_answered_question_as_skipped()
    {
        // A pick's awaits yield the dispatcher, so a backdrop click can genuinely arrive while the
        // pick for that same question is completing. Ungated, ClosePicker would file the question as
        // skipped — and the notice would then claim one ingredient both taken and left uncounted.
        SeedProduct("Ground Beef", counted: false, onHand: null, unit: null, 1m, 1m);
        var freezerId = SeedProduct("Quarter Cow Ground Beef", counted: true, onHand: 14m);
        SeedRecipe("Freezer Chili", "ground beef", "Ground Beef");
        var cut = RenderRecipes();
        ClickAteIt(cut, "Freezer Chili");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".picker-modal .alt-bubble")));

        var gate = new TaskCompletionSource();
        Factory.HoldNext = gate; // parks PickCandidate at its context create, eatBusy held true
        cut.FindAll(".picker-modal .alt-bubble")[0].Click();

        cut.Find(".picker-backdrop").Click(); // must NO-OP while the pick is in flight
        Assert.NotNull(cut.Find(".picker-modal"));            // the question did not get filed…
        Assert.DoesNotContain("Left uncounted", cut.Markup);  // …as skipped

        gate.SetResult();

        cut.WaitForAssertion(() =>
        {
            // The pick completes into Taken — once, and only there.
            Assert.Equal("Quarter Cow Ground Beef — 1 off, 13 left", Collapsed(cut.Find(".inline-confirm li")));
            Assert.Empty(cut.FindAll("[role=dialog]"));
            Assert.DoesNotContain("Left uncounted", cut.Markup);
        });

        // Exactly one decrement: the interleaved close neither doubled nor undid the take.
        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(13m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == freezerId)).QuantityOnHand);
        Assert.Single(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_dismiss_mid_pick_cannot_orphan_the_saved_decrement()
    {
        // The other interleaving: Dismiss nulls the state a completing pick records into. Ungated, the
        // decrement would land in the database with no notice and no Undo — a write the human can
        // neither see nor take back.
        SeedProduct("Ground Beef", counted: false, onHand: null, unit: null, 1m, 1m);
        var freezerId = SeedProduct("Quarter Cow Ground Beef", counted: true, onHand: 14m);
        SeedRecipe("Freezer Chili", "ground beef", "Ground Beef");
        var cut = RenderRecipes();
        ClickAteIt(cut, "Freezer Chili");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".picker-modal .alt-bubble")));

        var gate = new TaskCompletionSource();
        Factory.HoldNext = gate;
        cut.FindAll(".picker-modal .alt-bubble")[0].Click();

        cut.FindAll(".inline-confirm button").Single(b => b.TextContent.Trim() == "Dismiss").Click();

        gate.SetResult();

        cut.WaitForAssertion(() =>
        {
            // The notice survives, carrying the completed pick — with Undo still on offer beside it.
            Assert.Equal("Quarter Cow Ground Beef — 1 off, 13 left", Collapsed(cut.Find(".inline-confirm li")));
            Assert.Single(cut.FindAll(".inline-confirm button"), b => b.TextContent.Contains("Undo"));
        });

        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(13m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == freezerId)).QuantityOnHand);
    }

    // ------------------------------------------------------------------- a pick that finds nothing

    [Fact]
    public async Task A_pick_that_finds_nothing_to_take_lands_in_skipped()
    {
        // The shelf can move between the ask and the answer (another circuit, another cook). A pick
        // that finds nothing must not vanish — the ingredient went uncounted and the notice says so —
        // and it must not claim a decrement that never happened.
        var chuckId = SeedProduct("Ground Beef Chuck", counted: true, onHand: 4m, unit: null, 1m, 1m);
        var sirloinId = SeedProduct("Ground Beef Sirloin", counted: true, onHand: 6m, unit: null, 1m, 1m);
        SeedRecipe("Weeknight Tacos", "ground beef", null);
        var cut = RenderRecipes();
        ClickAteIt(cut, "Weeknight Tacos");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".picker-modal .alt-bubble").Count));

        // Empty the candidate about to be picked, between the modal rendering and the answer.
        using (var db = Db.CreateDbContext())
        {
            db.Products.Single(p => p.Id == chuckId).QuantityOnHand = 0m;
            db.SaveChanges();
        }

        cut.FindAll(".picker-modal .alt-bubble")[0].Click(); // the now-empty Ground Beef Chuck

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[role=dialog]")); // the question is spent either way
            Assert.Contains("Left uncounted — you didn't pick a product for: ground beef",
                Collapsed(cut.Find(".inline-confirm")));
            // Nothing was taken, so nothing may be reported taken — and a no-op is not a failure.
            Assert.Empty(cut.FindAll(".inline-confirm li"));
            Assert.Empty(cut.FindAll(".error[role=alert]"));
        });

        await using var raw = Db.CreateUnscopedContext();
        Assert.Equal(0m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == chuckId)).QuantityOnHand);
        Assert.Equal(6m, (await raw.Products.IgnoreQueryFilters().SingleAsync(p => p.Id == sirloinId)).QuantityOnHand);
        Assert.Single(await raw.MealEvents.IgnoreQueryFilters().ToListAsync());
    }
}
