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
        string name, bool tracked = true, bool counted = false, decimal? onHand = null,
        DateTimeOffset? countedAt = null, params DateOnly[] purchases)
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
            Purchases = [.. purchases.Select(d => new PurchaseEvent { PurchasedAt = d, Quantity = 1 })],
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    /// <summary>A product with buying history behind it — a learned rhythm, so predictions about it are
    /// meaningful. Purchase history does NOT change what a zero means (every zero writes its
    /// <c>OutNow</c>); it is here for the tests that need a cadence to exist.</summary>
    private Task<Product> SeedBoughtProduct(string name, bool tracked = true, bool counted = false, decimal? onHand = null) =>
        SeedProduct(name, tracked, counted, onHand, null,
            DateOnly.FromDateTime(DateTime.Today.AddDays(-40)), DateOnly.FromDateTime(DateTime.Today.AddDays(-12)));

    private static CensusConfirmationService.CensusRow R(
        string name, decimal? count, int productId = 0, Category category = Category.Frozen) =>
        new(name, category, count, productId);

    private async Task<Product> Reload(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Products.SingleAsync(p => p.Id == id);
    }

    // ---- twins: a shared name is not an address ----

    [Fact]
    public async Task An_unmatched_row_naming_twins_is_refused_not_guessed()
    {
        // No unique index exists on product names, and Attest REPLACES a count — so "which twin?"
        // is not a question the service may answer with First(). Both counts are asserted untouched:
        // landing on either would be a wrong answer, so the assertion covers both.
        var a = await SeedProduct("Sardines", counted: true, onHand: 5, countedAt: DateTimeOffset.Now);
        var b = await SeedProduct("Sardines", counted: true, onHand: 2, countedAt: DateTimeOffset.Now);

        var outcome = await _service.ConfirmAsync([R("Sardines", 9)]);

        Assert.Equal(0, outcome.Counted);
        var refusal = Assert.Single(outcome.Refused);
        Assert.Equal(CensusConfirmationService.CensusRefusal.AmbiguousName, refusal.Reason);
        Assert.Equal(5m, (await Reload(a.Id)).QuantityOnHand);
        Assert.Equal(2m, (await Reload(b.Id)).QuantityOnHand);
    }

    [Fact]
    public async Task A_row_naming_a_punctuation_twin_pair_is_refused_like_raw_twins()
    {
        // Raw equality resolved this row deterministically to the raw match — which is still a coin
        // flip about which jar the household MEANT, decided by how the vision model punctuated the
        // label. Ambiguity is the matcher's rule-1 identity set at every layer now, so no write can
        // land on a member of an ambiguous pair without an explicit id.
        var hyphenated = await SeedProduct("Home-Canned Tomato Sauce", counted: true, onHand: 9, countedAt: DateTimeOffset.Now);
        var spaced = await SeedProduct("Home Canned Tomato Sauce", counted: true, onHand: 2, countedAt: DateTimeOffset.Now);

        var outcome = await _service.ConfirmAsync([R("Home Canned Tomato Sauce", 4)]);

        Assert.Equal(0, outcome.Counted);
        var refusal = Assert.Single(outcome.Refused);
        Assert.Equal(CensusConfirmationService.CensusRefusal.AmbiguousName, refusal.Reason);
        Assert.Equal(9m, (await Reload(hyphenated.Id)).QuantityOnHand);
        Assert.Equal(2m, (await Reload(spaced.Id)).QuantityOnHand);
    }

    [Fact]
    public async Task One_census_reading_a_name_two_ways_makes_one_product_not_a_refused_pair()
    {
        // ⚠️ The re-gate's sharpest find: refusing on the matcher's identity rule while RESOLVING on
        // raw equality let one census MINT the punctuation pair — the reader emits a row per variety,
        // transcribing label text with and without a hyphen, both "new" — after which every later
        // census of that shelf was refused AmbiguousName forever. One identity set for refusal AND
        // resolution: the second row folds into the first row's product and the counts sum.
        var outcome = await _service.ConfirmAsync(
            [R("Home-Canned Sauce", 4), R("Home Canned Sauce", 2)]);

        Assert.Equal(1, outcome.Counted);
        Assert.Equal(2, outcome.Rows);
        Assert.Equal(1, outcome.NewProducts);
        Assert.Empty(outcome.Refused);
        await using var db = _db.CreateDbContext();
        var product = Assert.Single(await db.Products.ToListAsync());
        Assert.Equal(6m, product.QuantityOnHand);
    }

    [Fact]
    public async Task A_punctuation_variant_of_one_existing_product_resolves_to_it()
    {
        // The identity set has ONE member, so this is rule 1 — an identity, not a guess — and the
        // count lands where the household's product already lives instead of minting its twin.
        var existing = await SeedProduct("Home-Canned Sauce", counted: true, onHand: 9, countedAt: DateTimeOffset.Now);

        var outcome = await _service.ConfirmAsync([R("Home Canned Sauce", 4)]);

        Assert.Equal(1, outcome.Counted);
        Assert.Equal(0, outcome.NewProducts);
        await using var db = _db.CreateDbContext();
        Assert.Single(await db.Products.ToListAsync());
        Assert.Equal(4m, (await Reload(existing.Id)).QuantityOnHand);
    }

    [Fact]
    public async Task An_explicit_create_new_on_a_twin_name_is_still_the_duplicate_refusal()
    {
        // The order of the two refusals matters: a human who chose "create new" gets the answer about
        // THEIR choice (the name is taken), not a lecture about a pick they weren't making — and no
        // third twin appears either way.
        await SeedProduct("Sardines", counted: true, onHand: 5, countedAt: DateTimeOffset.Now);
        await SeedProduct("Sardines", counted: true, onHand: 2, countedAt: DateTimeOffset.Now);

        var outcome = await _service.ConfirmAsync(
            [new CensusConfirmationService.CensusRow("Sardines", Category.Frozen, 9, 0, CreateNew: true)]);

        var refusal = Assert.Single(outcome.Refused);
        Assert.Equal(CensusConfirmationService.CensusRefusal.DuplicateName, refusal.Reason);
        await using var db = _db.CreateDbContext();
        Assert.Equal(2, await db.Products.CountAsync());
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
        // own proposals at 1, so a zero reaching here was typed by a person. Bought before, because that
        // is the rhythm the outage teaches — and the only shape a zero is accepted against.
        var beef = await SeedBoughtProduct("Ground Beef", counted: true, onHand: 4);

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
        Assert.Equal(CensusConfirmationService.CensusRefusal.ZeroOnNewProduct, refused.Reason);
        Assert.Equal("Frozen Peas", refused.Name);
    }

    [Fact]
    public async Task A_zero_on_a_product_with_buying_history_is_still_a_real_outage()
    {
        // The complement, and the reason the refusal is scoped the way it is: §13.4's asserted zero on
        // something the household actually buys is real evidence, and the rhythm behind it is what can
        // later contradict the outage. This is the case that must keep working.
        var beans = await SeedBoughtProduct("Black Beans", counted: true, onHand: 4);

        var outcome = await _service.ConfirmAsync([R("Black Beans", 0, beans.Id)]);

        await using var db = _db.CreateDbContext();
        Assert.Equal(SignalKind.OutNow, Assert.Single(await db.InventorySignals.ToListAsync()).Kind);
        Assert.Equal(0m, (await Reload(beans.Id)).QuantityOnHand);
        Assert.Equal(1, outcome.AssertedOut);
        Assert.Empty(outcome.Refused);
    }

    [Fact]
    public async Task A_zero_on_a_product_with_no_purchases_still_records_the_outage()
    {
        // ⚠️ §13.8's own population (bought pre-app, gifted, bulk) has no purchase history by
        // construction, and its zero is treated exactly like any other. An attempt to WITHHOLD the
        // OutNow here was built and reverted: the reason given — that with no rhythm the pin could
        // never be cleared — is false. `lastStockBack` is the max of purchase dates AND restock dates,
        // so one Restocked tap clears it, on the dashboard card the pin itself creates. Withholding it
        // also removed the only thing holding the zero once the count went stale, so recipes began
        // offering food the household had counted as none. Both probed; see StockLedger.Attest.
        var sauce = await SeedProduct("Home-Canned Sauce", counted: true, onHand: 5);

        var outcome = await _service.ConfirmAsync([R("Home-Canned Sauce", 0, sauce.Id)]);

        await using var db = _db.CreateDbContext();
        Assert.Equal(SignalKind.OutNow, Assert.Single(await db.InventorySignals.ToListAsync()).Kind);
        Assert.Equal(0m, (await Reload(sauce.Id)).QuantityOnHand);
        Assert.Equal(1, outcome.AssertedOut);
        Assert.Empty(outcome.Refused);
    }

    [Fact]
    public async Task Two_rows_summing_to_zero_are_one_statement_not_two()
    {
        // The signal decision is made on the TOTAL, not per row — "I took the last two" plus "there are
        // none left" is one claim about the shelf. Bought here, so the outage IS filed.
        var beans = await SeedBoughtProduct("Black Beans", counted: true, onHand: 4);

        var outcome = await _service.ConfirmAsync([R("Black Beans", 0, beans.Id), R("Black Beans", 0, beans.Id)]);

        await using var db = _db.CreateDbContext();
        Assert.Single(await db.InventorySignals.ToListAsync()); // one signal, not two
        Assert.Equal(1, outcome.AssertedOut);
        Assert.Equal(2, outcome.Rows);
        Assert.Equal(1, outcome.Counted);
    }

    [Theory]
    [InlineData(true)]   // the zero row first
    [InlineData(false)]  // the zero row second
    public async Task A_zero_beside_a_real_count_of_the_same_new_item_reads_the_same_either_way(bool zeroFirst)
    {
        // ⚠️ Deciding a zero row's fate WHERE IT SAT made the outcome depend on the order the reader
        // happened to emit rows in: the zero-first ordering refused a row and then told the household
        // "nothing was created" about a product sitting on their Products page, while zero-second refused
        // nothing. The reader emits a row per variety and matches across varieties, so two rows naming one
        // new item is its ordinary output — not an edge case. Settled once, after every row is read.
        CensusConfirmationService.CensusRow[] rows = zeroFirst
            ? [R("Sardines", 0), R("Sardines", 2)]
            : [R("Sardines", 2), R("Sardines", 0)];

        var outcome = await _service.ConfirmAsync(rows);

        await using var db = _db.CreateDbContext();
        var product = await db.Products.SingleAsync();
        Assert.Equal("Sardines", product.Name);
        Assert.Equal(2m, product.QuantityOnHand);
        Assert.Empty(outcome.Refused);          // ← the half that failed in one ordering
        Assert.Equal(1, outcome.NewProducts);
        Assert.Equal(2, outcome.Rows);          // both rows landed
        Assert.Equal(1, outcome.Counted);
        Assert.Empty(await db.InventorySignals.ToListAsync());
    }

    [Fact]
    public async Task A_zero_row_is_still_refused_when_it_would_CREATE_the_product()
    {
        // The one zero with nothing to record: the catalog doesn't have it and the row says the shelf
        // doesn't either, so creating it would mint a phantom the household has never owned. The positive
        // row still lands — a refusal is per row, never per census.
        var outcome = await _service.ConfirmAsync([R("Frozen Peas", 3), R("Sardines", 0)]);

        await using var db = _db.CreateDbContext();
        Assert.Equal("Frozen Peas", (await db.Products.SingleAsync()).Name);
        Assert.Empty(await db.InventorySignals.ToListAsync());
        Assert.Equal(CensusConfirmationService.CensusRefusal.ZeroOnNewProduct,
            Assert.Single(outcome.Refused).Reason);
    }

    // ---- what an empty box means, and what a vanished product means ----

    [Fact]
    public async Task An_EMPTY_count_box_is_refused_and_is_never_read_as_an_outage()
    {
        // ⚠️ The sharpest edge on the page. A non-nullable bind turned a box cleared to retype into 0, and
        // a ticked 0 is an ASSERTED out — a real OutNow filed against a freezer full of the stuff, from a
        // field nobody typed in. Null means "nobody has said" and is refused, never coerced.
        var beans = await SeedBoughtProduct("Black Beans", counted: true, onHand: 5);

        var outcome = await _service.ConfirmAsync([R("Black Beans", null, beans.Id)]);

        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.InventorySignals.ToListAsync());
        Assert.Equal(5m, (await Reload(beans.Id)).QuantityOnHand); // the real count survives untouched
        Assert.Equal(0, outcome.AssertedOut);
        var refused = Assert.Single(outcome.Refused);
        Assert.Equal(CensusConfirmationService.CensusRefusal.MissingCount, refused.Reason);
        Assert.Equal("Black Beans", refused.Name);
    }

    [Fact]
    public async Task A_row_naming_a_product_that_has_since_vanished_is_refused_not_redirected()
    {
        // A merge or a delete in another tab while the grid sits open. Resolving by name instead would
        // create a twin of the product that just went away — or land the count on a DIFFERENT product
        // than the dropdown showed — and say nothing about either.
        var outcome = await _service.ConfirmAsync([R("Ground Beef", 4, productId: 4242)]);

        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.Products.ToListAsync());
        var refused = Assert.Single(outcome.Refused);
        Assert.Equal(CensusConfirmationService.CensusRefusal.ProductGone, refused.Reason);
        Assert.Equal("Ground Beef", refused.Name);
    }

    [Fact]
    public async Task A_refusal_names_the_row_by_its_product_when_the_name_box_is_blank()
    {
        // Blanking the name of a matched row is fine (it resolves by id), so a refusal on such a row must
        // not report "(unnamed)" — among thirty rows that names nothing, and the advice that goes with it
        // would tell someone to supply a name the row never needed.
        var beans = await SeedBoughtProduct("Black Beans", counted: true, onHand: 5);

        var outcome = await _service.ConfirmAsync([R("", -3, beans.Id)]);

        var refused = Assert.Single(outcome.Refused);
        Assert.Equal(CensusConfirmationService.CensusRefusal.Unusable, refused.Reason);
        Assert.Equal("Black Beans", refused.Name);
    }

    // ---- resuming a count that was deliberately stopped ----

    [Fact]
    public async Task Recounting_a_DORMANT_product_resumes_counting_and_says_so()
    {
        // Stop-counting is dormant, not destructive: the number and its date are kept as history. A census
        // overwrites both and starts believing them again — right for someone who just counted the shelf,
        // but it is the one switch they deliberately turned off, so the outcome reports it. `Retracked` is
        // a DIFFERENT property (IsTracked) and would stay silent here.
        var stopped = DateTimeOffset.Now.AddDays(-90);
        var chuck = await SeedProduct("Ground Chuck", counted: false, onHand: 14, countedAt: stopped);

        var outcome = await _service.ConfirmAsync([R("Ground Chuck", 3, chuck.Id)]);

        var reloaded = await Reload(chuck.Id);
        Assert.True(reloaded.TrackQuantity);
        Assert.Equal(3m, reloaded.QuantityOnHand);
        Assert.Equal(1, outcome.ResumedCounting);
        Assert.Equal(0, outcome.Retracked); // it was tracked all along — a different thing entirely
    }

    [Fact]
    public async Task A_never_counted_product_is_not_reported_as_resuming()
    {
        // One complement: "resumed" means a stored number was overwritten, not merely that counting began.
        // Without this the summary would announce a resumption on every ordinary first count.
        var fish = await SeedProduct("Tilapia Fillets");

        var outcome = await _service.ConfirmAsync([R("Tilapia Fillets", 2, fish.Id)]);

        Assert.True((await Reload(fish.Id)).TrackQuantity);
        Assert.Equal(0, outcome.ResumedCounting);
    }

    [Fact]
    public async Task An_ALREADY_COUNTED_product_is_not_reported_as_resuming()
    {
        // ⚠️ The other complement, and the one that was missing: the test above is satisfied by the
        // "has a stored number" half alone (a never-counted product has none), so deleting the
        // `!TrackQuantity` conjunct left the whole suite green while every ordinary recount announced
        // "Started counting 1 item you'd stopped counting" — a screen stating what the engine didn't do,
        // in the very rule added to stop that. A rule needs BOTH halves pinned or it is pinned by luck.
        var beans = await SeedProduct("Black Beans", counted: true, onHand: 5,
            countedAt: DateTimeOffset.Now.AddDays(-3));

        var outcome = await _service.ConfirmAsync([R("Black Beans", 2, beans.Id)]);

        Assert.Equal(2m, (await Reload(beans.Id)).QuantityOnHand); // the recount landed
        Assert.Equal(0, outcome.ResumedCounting);                  // but nothing "resumed"
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
        // row is REFUSED — B's own same-named product is not silently written to either. That second half
        // matters as much as the first: the boundary holding is what stops the reach-across, but a row
        // that lands somewhere other than the id it named is a different wrong answer, and a tampered
        // circuit message must not be able to steer a count by guessing at ids.
        var outcome = await _service.ConfirmAsync([R("Ground Beef", 7, mine.Id)]);

        Assert.Equal(CensusConfirmationService.CensusRefusal.ProductGone,
            Assert.Single(outcome.Refused).Reason);

        await using var raw = _db.CreateUnscopedContext();
        var all = await raw.Products.IgnoreQueryFilters().ToListAsync();
        Assert.Null(all.Single(p => p.Id == mine.Id).QuantityOnHand); // never reached across
        Assert.Null(all.Single(p => p.Id == theirs.Id).QuantityOnHand); // and not redirected onto B's own
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
        var beef = await SeedBoughtProduct("Ground Beef", counted: true, onHand: 2);

        await _service.ConfirmAsync([R("Ground Beef", 0, beef.Id)]);

        await using var raw = _db.CreateUnscopedContext();
        var signal = await raw.InventorySignals.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("hh-b", signal.HouseholdId);
    }
}
