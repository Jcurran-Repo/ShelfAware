using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Shopping;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// Eggs's lookalike nudge across its two surfaces: the gentle "roll them into one?" card on the grocery
/// list (merge either way — with an inline ↩ Undo — or permanently dismiss) and the "you told Eggs these
/// are separate" list on a product's detail page (with an undo). Real SQLite via the shared harness, so
/// the merge, the memory, and the undo are the production ones.
/// </summary>
public class LookalikeNudgePageTests : PageTestContext
{
    private static List<PurchaseEvent> Overdue() =>
    [
        new() { PurchasedAt = Today.AddDays(-45), Quantity = 1m },
        new() { PurchasedAt = Today.AddDays(-30), Quantity = 1m },
    ];

    // Two tracked products that read as two names for one bread — one lookalike pair. Overdue so they also sit on the
    // visible list. Returns the ids lowest-first (so the first is the canonical "lower").
    private (int LowerId, int HigherId) SeedPair(string a, string b)
    {
        using var db = Db.CreateDbContext();
        var first = new Product { Name = a, Category = Category.Pantry, Purchases = Overdue() };
        var second = new Product { Name = b, Category = Category.Pantry, Purchases = Overdue() };
        db.Products.AddRange(first, second);
        db.SaveChanges();
        return first.Id < second.Id ? (first.Id, second.Id) : (second.Id, first.Id);
    }

    // Jordan's breads: "Artesano Brioche Bread" (seeded first, so lower id) and "Brioche Bread Loaf" share brioche + bread.
    private (int BreadId, int LoafId) SeedLookalikePair() => SeedPair("Artesano Brioche Bread", "Brioche Bread Loaf");

    private IRenderedComponent<GroceryList> RenderList()
    {
        var cut = Render<GroceryList>();
        cut.WaitForState(() => cut.FindAll(".extras").Count > 0);
        return cut;
    }

    private LookalikeNudgeService Nudges => Services.GetRequiredService<LookalikeNudgeService>();

    // --------------------------------------------------------------------------- grocery-list nudge

    [Fact]
    public void The_list_nudges_about_two_lookalike_products_with_the_real_mascot()
    {
        SeedLookalikePair();

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            var nudge = cut.Find(".nudge");
            Assert.NotNull(nudge.QuerySelector(".eggs-mascot"));                 // the real Eggs, not a placeholder
            Assert.Contains("Artesano Brioche Bread", nudge.TextContent);
            Assert.Contains("Brioche Bread Loaf", nudge.TextContent);
            Assert.Contains("roll them into one", nudge.TextContent);            // the ask stays gentle
        });
    }

    [Fact]
    public async Task Merging_from_the_nudge_keeps_the_chosen_name_and_offers_an_undo()
    {
        var (breadId, loafId) = SeedLookalikePair();
        var cut = RenderList();
        cut.WaitForState(() => cut.FindAll(".nudge").Count > 0);

        // Keep "Artesano Brioche Bread" — the loaf folds into it.
        cut.FindAll(".nudge-actions button").Single(b => b.TextContent.Trim() == "Artesano Brioche Bread").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".nudge"));                                 // one product left ⇒ no pair
            var done = cut.Find(".nudge-done");
            Assert.Contains("Merged Brioche Bread Loaf into Artesano Brioche Bread", done.TextContent);
        });

        await using (var raw = Db.CreateUnscopedContext())
        {
            Assert.NotNull(await raw.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == breadId));
            Assert.Null(await raw.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Id == loafId)); // folded away
        }

        // ↩ Undo brings the loaf back (a new row — merge undo rebuilds the source), so both exist again.
        cut.Find(".nudge-done button").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".nudge-done")));

        await using var db = Db.CreateUnscopedContext();
        Assert.NotNull(await db.Products.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Name == "Brioche Bread Loaf"));
    }

    [Fact]
    public void At_most_three_nudges_show_and_the_rest_sit_behind_an_overflow_note()
    {
        // Four lookalike pairs (a catalog dense with near-twins). Only three cards show so the actual list
        // isn't buried; the fourth is behind a gentle overflow note.
        SeedPair("Brioche Bread", "Brioche Bread Loaf");
        SeedPair("Sourdough Boule", "Fresh Sourdough Boule");
        SeedPair("Cheddar Cheese", "Sharp Cheddar Cheese");
        SeedPair("Roma Tomatoes", "Cherry Tomatoes");

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, cut.FindAll(".nudge").Count);
            Assert.Contains("1 more look-alike", cut.Find(".nudge-overflow").TextContent);
        });
    }

    [Fact]
    public void Dismissing_after_a_merge_clears_the_stale_merged_notice()
    {
        SeedPair("Brioche Bread", "Brioche Bread Loaf");
        SeedPair("Hand Soap", "Dish Soap");
        var cut = RenderList();
        cut.WaitForState(() => cut.FindAll(".nudge").Count == 2);

        // Merge the breads → the inline "Merged…" notice appears.
        cut.FindAll(".nudge-actions button").Single(b => b.TextContent.Trim() == "Brioche Bread").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".nudge-done")));

        // Dismiss the remaining pair → a new action supersedes the stale merge notice.
        cut.FindAll(".nudge-actions .linkish").First().Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".nudge-done")));
    }

    [Fact]
    public void Dismissing_a_nudge_makes_it_stop_permanently()
    {
        SeedLookalikePair();
        var cut = RenderList();
        cut.WaitForState(() => cut.FindAll(".nudge").Count > 0);

        cut.Find(".nudge-actions .linkish").Click(); // "They're different"

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".nudge")));

        // A fresh render (a later visit) must not resurrect it — the dismissal is remembered.
        var again = RenderList();
        again.WaitForState(() => again.FindAll(".extras").Count > 0);
        Assert.Empty(again.FindAll(".nudge"));
    }

    // --------------------------------------------------------------------------- product-detail dismissal

    private IRenderedComponent<ProductDetail> RenderDetail(int id)
    {
        var cut = Render<ProductDetail>(ps => ps.Add(p => p.Id, id));
        cut.WaitForState(() => cut.FindAll("h1").Count > 0);
        return cut;
    }

    [Fact]
    public async Task A_dismissed_pair_is_listed_on_the_product_page_and_can_be_brought_back()
    {
        var (breadId, loafId) = SeedLookalikePair();
        // Record the pair, then dismiss it (as the grocery list would).
        var now = new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await Nudges.GetActiveAsync([.. await Load()], now);
        await Nudges.DismissAsync(breadId, loafId, now);

        var cut = RenderDetail(breadId);

        cut.WaitForAssertion(() =>
        {
            var section = cut.Find(".dismissed-lookalikes");
            Assert.Contains("Brioche Bread Loaf", section.TextContent);               // names the OTHER product
        });

        cut.Find(".dismissed-lookalikes button").Click(); // "Bring the suggestion back"

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".dismissed-lookalikes")));
        // It's active again — the service now returns it as a nudge.
        Assert.Single((await Nudges.GetActiveAsync([.. await Load()], now)).Pairs);

        async Task<List<Product>> Load()
        {
            await using var db = Db.CreateUnscopedContext();
            return await db.Products.IgnoreQueryFilters().ToListAsync();
        }
    }

    [Fact]
    public void An_active_lookalike_shows_no_dismissed_section_on_the_product_page()
    {
        // A pair that's flagged but never dismissed must not appear in the "you told Eggs these are separate"
        // list — that list is for dismissals only.
        var (breadId, _) = SeedLookalikePair();

        var cut = RenderDetail(breadId);

        cut.WaitForState(() => cut.FindAll("h1").Count > 0);
        Assert.Empty(cut.FindAll(".dismissed-lookalikes"));
    }

    // --------------------------------------------------------------------------- cluster card

    // One tracked, overdue product (so it sits on the visible list). Returns its id.
    private int SeedProduct(string name)
    {
        using var db = Db.CreateDbContext();
        var p = new Product { Name = name, Category = Category.Pantry, Purchases = Overdue() };
        db.Products.Add(p);
        db.SaveChanges();
        return p.Id;
    }

    // Three dog treats, two of them Dentastix twins: a cluster (head "treat") that gets exactly ONE card. Returns the ids in name order.
    private (int Dentastix, int DentastixBacon, int Jerky) SeedTreatCluster() =>
        (SeedProduct("Dentastix Large Breed Dog Treats"), SeedProduct("Chicken Jerky Dog Treats"), SeedProduct("Dentastix Bacon Large Breed Dog Treats"));

    private async Task<List<Product>> LoadAll()
    {
        await using var db = Db.CreateUnscopedContext();
        return await db.Products.IgnoreQueryFilters().ToListAsync();
    }

    [Fact]
    public void A_cluster_gets_one_card_naming_its_members_and_no_pair_cards()
    {
        var (dentastix, dentastixBacon, jerky) = SeedTreatCluster();

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            var card = Assert.Single(cut.FindAll(".nudge"));                    // ONE card for the whole cluster
            Assert.Contains("nudge-cluster", card.ClassName);
            Assert.NotNull(card.QuerySelector(".eggs-mascot"));
            Assert.Equal(NudgeMoods.ClusterLine(NudgeMood.Fresh), card.QuerySelector(".nudge-line")!.TextContent.Trim()); // a group line, never "these two"
            Assert.Contains("3", card.QuerySelector(".nudge-pair")!.TextContent);
            Assert.Contains("treat", card.QuerySelector(".nudge-pair")!.TextContent);
            foreach (var id in new[] { dentastix, dentastixBacon, jerky })
                Assert.NotNull(card.QuerySelector($".nudge-members a[href='/product/{id}']")); // names link to the merge panel
            Assert.Empty(cut.FindAll(".nudge-actions button.secondary"));       // no keep/merge pair buttons
        });
    }

    [Fact]
    public void Dismissing_a_cluster_makes_it_stop_permanently()
    {
        SeedTreatCluster();
        var cut = RenderList();
        cut.WaitForState(() => cut.FindAll(".nudge-cluster").Count == 1);

        cut.Find(".nudge-cluster .nudge-actions button").Click(); // "They're all different"

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".nudge")));
        // Permanent: a fresh visit shows nothing either.
        var again = RenderList();
        again.WaitForState(() => again.FindAll(".extras").Count > 0);
        Assert.Empty(again.FindAll(".nudge"));
    }

    [Fact]
    public async Task A_dismissed_cluster_is_listed_on_a_member_s_page_and_can_be_brought_back()
    {
        var (dentastix, _, _) = SeedTreatCluster();
        var now = new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await Nudges.GetActiveAsync(await LoadAll(), now);
        await Nudges.DismissClusterAsync("treat", now);

        var cut = RenderDetail(dentastix);

        cut.WaitForAssertion(() =>
        {
            var section = cut.Find(".dismissed-cluster");
            Assert.Contains("treat", section.TextContent);
            Assert.Contains("Chicken Jerky Dog Treats", section.TextContent); // the OTHER members, not itself
            Assert.Contains("Dentastix Bacon Large Breed Dog Treats", section.TextContent);
            Assert.DoesNotContain("Dentastix Large Breed Dog Treats", section.TextContent);
        });

        cut.Find(".dismissed-cluster button").Click(); // "Bring the suggestion back"

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".dismissed-cluster")));
        Assert.Single((await Nudges.GetActiveAsync(await LoadAll(), now)).Clusters); // active again
    }

    [Fact]
    public void Pair_and_cluster_cards_share_the_three_card_cap()
    {
        // Three pairs plus one cluster: four nudges of two kinds, ranked together — three cards, one behind
        // the overflow note, whichever shape it is.
        SeedPair("Brioche Bread", "Brioche Bread Loaf");
        SeedPair("Sourdough Boule", "Fresh Sourdough Boule");
        SeedPair("Cheddar Cheese", "Sharp Cheddar Cheese");
        SeedTreatCluster();

        var cut = RenderList();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, cut.FindAll(".nudge").Count);
            Assert.Contains("1 more look-alike", cut.Find(".nudge-overflow").TextContent);
        });
    }
}
