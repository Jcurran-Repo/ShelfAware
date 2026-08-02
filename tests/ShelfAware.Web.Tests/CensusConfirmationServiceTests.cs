using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The census write path (DESIGN.md §13.8) over real EF on real SQLite. The rule this suite exists for is
/// the ★ one — a census records what you HAVE and never what you bought — and it is asserted first,
/// because it is the one whose violation would be invisible until every rhythm in the app had drifted.
/// </summary>
public class CensusConfirmationServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly CensusConfirmationService _service;

    public CensusConfirmationServiceTests() => _service = new CensusConfirmationService(_db);

    public void Dispose() => _db.Dispose();

    private async Task<Product> SeedProduct(
        string name, bool tracked = true, bool counted = false, decimal? onHand = null, DateTimeOffset? countedAt = null)
    {
        await using var db = _db.CreateDbContext();
        var product = new Product
        {
            Name = name,
            Category = Category.Frozen,
            IsTracked = tracked,
            TrackQuantity = counted,
            QuantityOnHand = onHand,
            QuantityCountedAt = countedAt,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static CensusConfirmationService.CensusRow R(
        string name, decimal count, int productId = 0, Category category = Category.Frozen) =>
        new(name, category, count, productId);

    private async Task<Product> Reload(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Products.SingleAsync(p => p.Id == id);
    }

    // ---- ★ the rule the whole service exists to hold ----

    [Fact]
    public async Task A_census_never_records_a_purchase()
    {
        // §13.8's ★ rule. You did not buy the contents of your freezer today, and every rhythm in the app
        // is learned from purchase dates — so an invented PurchaseEvent here would quietly re-date the
        // household's whole buying history. This is why the census does not reuse the receipt confirm path.
        var beef = await SeedProduct("Ground Beef");

        await _service.ConfirmAsync([R("Ground Beef", 6, beef.Id), R("Frozen Peas", 3)]);

        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.PurchaseEvents.ToListAsync());
        Assert.Empty(await db.Receipts.ToListAsync());
    }

    // ---- attesting ----

    [Fact]
    public async Task A_counted_row_attests_the_number_and_opts_the_product_in()
    {
        var beef = await SeedProduct("Ground Beef");
        var before = DateTimeOffset.Now;

        var outcome = await _service.ConfirmAsync([R("Ground Beef", 6, beef.Id)]);

        var saved = await Reload(beef.Id);
        Assert.True(saved.TrackQuantity);
        Assert.Equal(6m, saved.QuantityOnHand);
        // A census is a LOOK at the shelf, so it re-anchors the clock §13.5's drift check measures from.
        Assert.NotNull(saved.QuantityCountedAt);
        Assert.True(saved.QuantityCountedAt >= before);
        Assert.Equal(1, outcome.Counted);
    }

    [Fact]
    public async Task A_recount_replaces_the_old_number_rather_than_adding_to_it()
    {
        var beans = await SeedProduct("Black Beans", counted: true, onHand: 3,
            countedAt: DateTimeOffset.Now.AddDays(-100));

        await _service.ConfirmAsync([R("Black Beans", 9, beans.Id)]);

        var saved = await Reload(beans.Id);
        Assert.Equal(9m, saved.QuantityOnHand);
        Assert.True(saved.QuantityCountedAt > DateTimeOffset.Now.AddDays(-1));
    }

    [Fact]
    public async Task A_fractional_count_survives_for_a_weight_item()
    {
        // §13.1: the count is decimal because weight items are fractional in their own unit. The reader
        // can only propose a whole number; the person editing the row can type 2.5 lb.
        var beef = await SeedProduct("Ground Chuck");

        await _service.ConfirmAsync([R("Ground Chuck", 2.5m, beef.Id)]);

        Assert.Equal(2.5m, (await Reload(beef.Id)).QuantityOnHand);
    }

    // ---- products ----

    [Fact]
    public async Task An_unmatched_row_creates_a_product_with_no_receipt_provenance()
    {
        var outcome = await _service.ConfirmAsync([R("Home-Canned Tomato Sauce", 12, category: Category.Pantry)]);

        await using var db = _db.CreateDbContext();
        var created = await db.Products.SingleAsync(p => p.Name == "Home-Canned Tomato Sauce");
        Assert.Equal(Category.Pantry, created.Category);
        Assert.Equal(12m, created.QuantityOnHand);
        // No receipt introduced it, so claiming one would offer it up to that receipt's removal.
        Assert.Null(created.CreatedByReceiptId);
        Assert.Equal(1, outcome.NewProducts);
    }

    [Fact]
    public async Task An_unmatched_row_whose_name_already_exists_resolves_to_that_product()
    {
        // A census is the app's biggest bulk product creator, and a twin splits purchase history and blinds
        // the predictor. It is also what makes a retry safe — see the test below. (Scoped to rows the grid
        // never matched; an explicit create-new is refused instead — see the gate-findings section.)
        var beef = await SeedProduct("Ground Beef");

        var outcome = await _service.ConfirmAsync([R("ground beef", 4)]); // different casing, no id

        await using var db = _db.CreateDbContext();
        Assert.Single(await db.Products.ToListAsync());
        Assert.Equal(4m, (await Reload(beef.Id)).QuantityOnHand);
        Assert.Equal(0, outcome.NewProducts);
    }

    [Fact]
    public async Task Re_running_the_same_census_cannot_double_anything()
    {
        // The realistic road here is a confirm that commits and then fails on the way back: the obvious
        // move is to press the button again. Counts are TOTALS and unmatched names resolve to the products
        // the first run created, so the second run is a no-op rather than a duplicate pantry.
        await _service.ConfirmAsync([R("Frozen Peas", 5), R("Black Beans", 3)]);
        await _service.ConfirmAsync([R("Frozen Peas", 5), R("Black Beans", 3)]);

        await using var db = _db.CreateDbContext();
        var products = await db.Products.ToListAsync();
        Assert.Equal(2, products.Count);
        Assert.Equal(5m, products.Single(p => p.Name == "Frozen Peas").QuantityOnHand);
        Assert.Empty(await db.PurchaseEvents.ToListAsync());
    }

    [Fact]
    public async Task Two_rows_for_one_new_item_become_one_product_carrying_the_SUM()
    {
        var outcome = await _service.ConfirmAsync([R("Frozen Peas", 2), R("Frozen Peas", 3)]);

        await using var db = _db.CreateDbContext();
        var product = Assert.Single(await db.Products.ToListAsync());
        // Asserting the number, not just the row count — at 2 or 3 this would still be "one product".
        Assert.Equal(5m, product.QuantityOnHand);
        Assert.Equal(1, outcome.Counted);
        Assert.Equal(2, outcome.Rows);
    }

    [Fact]
    public async Task Counting_an_ignored_product_starts_predicting_it_again()
    {
        // Counting an item is a deliberate statement of interest in it. Without this the count would land
        // on a product no prediction, list, or dashboard ever reads — a silent no-op on the one row the
        // household had just gone to the trouble of counting.
        var beef = await SeedProduct("Ground Beef", tracked: false);

        var outcome = await _service.ConfirmAsync([R("Ground Beef", 6, beef.Id)]);

        Assert.True((await Reload(beef.Id)).IsTracked);
        Assert.Equal(1, outcome.Retracked);
    }

    // ---- summing, the silent-overwrite trap ----

    [Fact]
    public async Task Two_rows_resolving_to_one_product_are_SUMMED_not_overwritten()
    {
        // ⚠️ An attestation states a TOTAL. Attesting row by row would let the second row silently replace
        // the first, so a household with five would be left believing they had two — with nothing on
        // screen saying a number had been dropped.
        var beef = await SeedProduct("Ground Beef");

        var outcome = await _service.ConfirmAsync([R("Ground Beef", 3, beef.Id), R("Ground Beef", 2, beef.Id)]);

        Assert.Equal(5m, (await Reload(beef.Id)).QuantityOnHand);
        Assert.Equal(1, outcome.Counted); // one product, counted once
    }

    [Fact]
    public async Task Rows_that_only_agree_by_NAME_are_summed_the_same_way()
    {
        var peas = await SeedProduct("Frozen Peas");

        await _service.ConfirmAsync([R("Frozen Peas", 4, peas.Id), R("frozen peas", 1)]);

        Assert.Equal(5m, (await Reload(peas.Id)).QuantityOnHand);
    }

    // ---- zero, §13.4's load-bearing rule ----

    [Fact]
    public async Task A_human_counting_zero_records_a_real_outage()
    {
        // §13.4: an ASSERTED zero is real evidence and feeds the burn-rate rhythm. The reader floors its
        // own proposals at 1, so a zero reaching here was typed by a person.
        var beef = await SeedProduct("Ground Beef", counted: true, onHand: 4, countedAt: DateTimeOffset.Now.AddDays(-30));

        var outcome = await _service.ConfirmAsync([R("Ground Beef", 0, beef.Id)]);

        await using var db = _db.CreateDbContext();
        var signal = Assert.Single(await db.InventorySignals.ToListAsync());
        Assert.Equal(SignalKind.OutNow, signal.Kind);
        Assert.Equal(beef.Id, signal.ProductId);
        Assert.Equal(0m, (await Reload(beef.Id)).QuantityOnHand);
        Assert.Equal(1, outcome.AssertedOut);
    }

    [Fact]
    public async Task A_positive_count_records_no_signal_at_all()
    {
        var beef = await SeedProduct("Ground Beef");

        await _service.ConfirmAsync([R("Ground Beef", 6, beef.Id)]);

        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.InventorySignals.ToListAsync());
    }

    // ---- refusals ----

    [Fact]
    public async Task A_negative_count_is_refused_rather_than_clamped_to_an_outage()
    {
        // StockLedger.Attest floors at zero, and a floored "-3" would land on an ASSERTED out — writing a
        // real OutNow into the cadence engine off a typo. Same rule, same reason, as SetQuantityAsync's.
        var beef = await SeedProduct("Ground Beef", counted: true, onHand: 4);

        var outcome = await _service.ConfirmAsync([R("Ground Beef", -3, beef.Id)]);

        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.InventorySignals.ToListAsync());
        Assert.Equal(4m, (await Reload(beef.Id)).QuantityOnHand); // untouched
        Assert.Equal(CensusConfirmationService.CensusRefusal.Unusable, Assert.Single(outcome.Refused).Reason);
        Assert.Equal(0, outcome.Counted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_refused_when_there_is_no_product_to_resolve_by(string name)
    {
        var outcome = await _service.ConfirmAsync([R(name, 3)]);

        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Equal(CensusConfirmationService.CensusRefusal.Unusable, Assert.Single(outcome.Refused).Reason);
    }

    [Fact]
    public async Task A_blank_name_is_FINE_when_the_row_names_its_product_by_id()
    {
        // The name is only needed to resolve BY name. Clearing the Item box on a matched row — to retype
        // it, or because the match makes the name look irrelevant — used to drop the row silently and then
        // complain about a field that row never needed.
        var beef = await SeedProduct("Ground Beef");

        var outcome = await _service.ConfirmAsync([R("", 5, beef.Id)]);

        Assert.Equal(5m, (await Reload(beef.Id)).QuantityOnHand);
        Assert.Empty(outcome.Refused);
        Assert.Equal(1, outcome.Counted);
    }

    [Fact]
    public async Task A_refused_row_does_not_stop_the_rest_of_the_census()
    {
        var outcome = await _service.ConfirmAsync([R("Frozen Peas", 3), R("", 2), R("Black Beans", -1)]);

        await using var db = _db.CreateDbContext();
        Assert.Single(await db.Products.ToListAsync());
        Assert.Equal(1, outcome.Counted);
        Assert.Equal(2, outcome.Refused.Count);
    }

    [Fact]
    public async Task An_empty_census_writes_nothing_and_says_so()
    {
        var outcome = await _service.ConfirmAsync([]);

        // Field by field, not record equality: Refused is a list, and records compare lists by REFERENCE,
        // so an == against a fresh empty list fails no matter what the service returned.
        Assert.Equal(0, outcome.Counted);
        Assert.Equal(0, outcome.Rows);
        Assert.Equal(0, outcome.NewProducts);
        Assert.Equal(0, outcome.Retracked);
        Assert.Equal(0, outcome.AssertedOut);
        Assert.Empty(outcome.Refused);

        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Empty(await db.InventorySignals.ToListAsync());
    }

    // ---- the two the pre-push gate found ----

    [Fact]
    public async Task An_explicit_create_new_NEVER_overwrites_the_product_it_collides_with()
    {
        // ⚠️ The bug this replaces: "create new" and "we never matched it" both arrive as ProductId 0, so
        // the name fallback resolved an explicit create-new onto the existing product and REPLACED its
        // count — 12 packs silently becoming 4, no new product, and a summary that said nothing unusual.
        // The screen said "new product" while the write destroyed an old one.
        var beef = await SeedProduct("Ground Beef", counted: true, onHand: 12);

        var outcome = await _service.ConfirmAsync(
            [new CensusConfirmationService.CensusRow("Ground Beef", Category.Meat, 4m, 0, CreateNew: true)]);

        await using var db = _db.CreateDbContext();
        Assert.Single(await db.Products.ToListAsync());          // no twin created either
        Assert.Equal(12m, (await Reload(beef.Id)).QuantityOnHand); // and the 12 survives
        var refused = Assert.Single(outcome.Refused);
        Assert.Equal(CensusConfirmationService.CensusRefusal.DuplicateName, refused.Reason);
        Assert.Equal("Ground Beef", refused.Name);
        Assert.Equal(0, outcome.Counted);
    }

    [Fact]
    public async Task An_UNMATCHED_row_still_resolves_to_the_same_named_product()
    {
        // The other half of the same rule: without an explicit create-new the fallback still fires, which
        // is what keeps a retry from duplicating the catalog.
        var beef = await SeedProduct("Ground Beef", counted: true, onHand: 12);

        var outcome = await _service.ConfirmAsync([R("ground beef", 4)]); // CreateNew defaults false

        Assert.Equal(4m, (await Reload(beef.Id)).QuantityOnHand);
        Assert.Empty(outcome.Refused);
    }

    [Fact]
    public async Task Two_explicit_create_new_rows_of_one_name_still_make_ONE_product()
    {
        // No existing product to collide with, so both rows are creating the same thing — and creating it
        // twice would be the twin the duplicate guard exists to stop.
        var outcome = await _service.ConfirmAsync(
        [
            new CensusConfirmationService.CensusRow("Quarter Cow Ground Beef", Category.Meat, 8m, 0, CreateNew: true),
            new CensusConfirmationService.CensusRow("Quarter Cow Ground Beef", Category.Meat, 6m, 0, CreateNew: true),
        ]);

        await using var db = _db.CreateDbContext();
        var product = Assert.Single(await db.Products.ToListAsync());
        Assert.Equal(14m, product.QuantityOnHand);
        Assert.Equal(1, outcome.NewProducts);
        Assert.Empty(outcome.Refused);
    }

    [Fact]
    public async Task Counting_ZERO_of_something_that_does_not_exist_yet_is_refused()
    {
        // ⚠️ It used to CREATE the product and then assert an outage against it: a real OutNow pinning a
        // brand-new product Overdue at the top of the dashboard and the grocery list, forever. The row
        // arrives ticked, and typing 0 is what the page's own "fix the numbers" copy invites. You cannot
        // run out of something you have never had.
        var outcome = await _service.ConfirmAsync([R("Frozen Peas", 0)]);

        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Empty(await db.InventorySignals.ToListAsync());
        var refused = Assert.Single(outcome.Refused);
        Assert.Equal(CensusConfirmationService.CensusRefusal.OutageOnNewProduct, refused.Reason);
        Assert.Equal("Frozen Peas", refused.Name);
    }

    [Fact]
    public async Task A_zero_on_a_product_that_already_exists_is_still_a_real_outage()
    {
        // The complement, and the reason the refusal above is scoped to NEW products: §13.4's asserted zero
        // on something the household actually tracks is real evidence and must keep working.
        var beans = await SeedProduct("Black Beans", counted: true, onHand: 4);

        var outcome = await _service.ConfirmAsync([R("Black Beans", 0, beans.Id)]);

        await using var db = _db.CreateDbContext();
        Assert.Equal(SignalKind.OutNow, Assert.Single(await db.InventorySignals.ToListAsync()).Kind);
        Assert.Equal(0m, (await Reload(beans.Id)).QuantityOnHand);
        Assert.Equal(1, outcome.AssertedOut);
        Assert.Empty(outcome.Refused);
    }

    [Fact]
    public async Task A_zero_row_is_fine_once_another_row_has_created_the_product()
    {
        // The refusal keys on "would this row bring the product into existence", not on the number alone.
        var outcome = await _service.ConfirmAsync([R("Frozen Peas", 3), R("Frozen Peas", 0)]);

        await using var db = _db.CreateDbContext();
        Assert.Equal(3m, (await db.Products.SingleAsync()).QuantityOnHand);
        Assert.Empty(outcome.Refused);
    }

    // ---- tenancy ----

    [Fact]
    public async Task A_census_stamps_its_own_household_and_cannot_reach_another()
    {
        _db.HouseholdId = "hh-a";
        var mine = await SeedProduct("Ground Beef");

        _db.HouseholdId = "hh-b";
        var theirs = await SeedProduct("Ground Beef");

        // Household B counts, naming A's product id outright. The filtered lookup never resolves it, so the
        // row falls through to B's own same-named product rather than reaching across the boundary.
        await _service.ConfirmAsync([R("Ground Beef", 7, mine.Id)]);

        await using var raw = _db.CreateUnscopedContext();
        var all = await raw.Products.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(7m, all.Single(p => p.Id == theirs.Id).QuantityOnHand);
        Assert.Null(all.Single(p => p.Id == mine.Id).QuantityOnHand);
    }

    [Fact]
    public async Task A_product_a_census_creates_belongs_to_the_counting_household()
    {
        _db.HouseholdId = "hh-b";

        await _service.ConfirmAsync([R("Quarter Cow Ground Beef", 20)]);

        await using var raw = _db.CreateUnscopedContext();
        var created = await raw.Products.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("hh-b", created.HouseholdId);
    }

    [Fact]
    public async Task An_asserted_outage_is_stamped_to_the_counting_household_too()
    {
        _db.HouseholdId = "hh-b";
        var beef = await SeedProduct("Ground Beef", counted: true, onHand: 2);

        await _service.ConfirmAsync([R("Ground Beef", 0, beef.Id)]);

        await using var raw = _db.CreateUnscopedContext();
        var signal = await raw.InventorySignals.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("hh-b", signal.HouseholdId);
    }
}
