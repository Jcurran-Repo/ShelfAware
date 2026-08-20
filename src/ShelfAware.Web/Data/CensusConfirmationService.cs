using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Census;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Undo;

namespace ShelfAware.Web.Data;

/// <summary>
/// The ONE path that turns a reviewed shelf photo into counts (DESIGN.md §13.8) — and deliberately NOT
/// <see cref="ReceiptConfirmationService"/>, which is otherwise the app's one confirm path.
/// <para>★ <b>A census must never create a <see cref="PurchaseEvent"/>.</b> You did not buy the contents of
/// your freezer today; recording that you did would invent purchase dates, and every rhythm in the app is
/// learned from purchase dates. So a census writes products that did not exist yet, an attested count, and
/// — only ever from a human's typed zero — the <c>OutNow</c> that §13.4 says such a zero owes. The census
/// reuses the receipt review GRID, and that similarity is precisely why this
/// has its own service — "reuses the extraction shape end to end" is true of the line contract and false of
/// the writing.</para>
/// <para><b>What decides each row's fate lives in Core.</b> <see cref="CensusPlan"/> is the one pure
/// function the review grid and this service both consume, so the screen and the write cannot disagree about
/// which product a row lands on. This service does the EF: it asks the plan, then attests, creates, and
/// signals exactly as the plan says. One SaveChanges, one transaction — a failure persists nothing, so a
/// half-recorded census can't leave counts for the top of the shelf and none for the bottom.</para>
/// </summary>
public class CensusConfirmationService(IHouseholdDbFactory dbFactory, IActivityLog activityLog)
{
    /// <param name="ProductId">The product this row resolved to, or 0 for none.</param>
    /// <param name="CreateNew">⚠️ Whether the HUMAN explicitly chose "create a new product" for this row,
    /// as opposed to the grid simply never having matched it. Both arrive as <paramref name="ProductId"/>
    /// 0, and collapsing them is a real bug this once had: an explicit create-new whose name is taken must be
    /// declined and named (<see cref="CensusRefusal.DuplicateName"/>), not silently resolved onto the
    /// existing product and REPLACE its count — the screen saying "new product" while the write destroyed an
    /// old one.</param>
    /// <param name="Count">What the human says is there, or null when the box is EMPTY. ⚠️ Null is not
    /// zero and must never be coerced to one: an attested zero writes a real <c>OutNow</c> (§13.4), so
    /// `?? 0` would turn a box someone cleared to retype into a household statement that they had run
    /// out. Same rule, same reason, as ProductDetail's count panel. Decimal because a weight item's count
    /// is fractional in its own unit (§13.1) — the reader can only ever propose a whole number, but the
    /// person editing the row can type 2.5.</param>
    public record CensusRow(string Name, Category Category, decimal? Count, int ProductId, bool CreateNew = false);

    /// <summary>Why a row could not be recorded. An enum rather than a bare count because the cases need
    /// genuinely different sentences — a typo, an empty box, a product that vanished, a name clash the
    /// household can resolve, and a claim the app declines to make on their behalf.</summary>
    public enum CensusRefusal
    {
        /// <summary>Nothing to record: a negative count, or no name to resolve by.</summary>
        Unusable,
        /// <summary>An explicit "create new" whose name is already taken. Creating the twin would split
        /// purchase history and blind the predictor; silently merging would overrule the human's choice.
        /// So the row is declined and named, and they pick which they meant.</summary>
        DuplicateName,
        /// <summary>A count of zero on a row that would CREATE the product. There is nothing to record:
        /// the item is not in the catalog and the row says none of it is on the shelf either, so the only
        /// possible outcome is a phantom product the household has never owned.
        /// <para>⚠️ Scoped to creation, and nothing wider. Every other zero — including one on a product
        /// with no purchase history, which is §13.8's whole population — is recorded as a number AND
        /// writes its <c>OutNow</c>, exactly as on every other surface. A rule withholding the signal for
        /// rhythm-less products was built and reverted; see <c>StockLedger.Attest</c> for why, and do not
        /// rebuild it.</para>
        /// <para>Decided once, after every row has been read (see <see cref="CensusPlan.Plan"/>), rather
        /// than where the row sits: two rows naming one new item is the reader's ordinary output, so a
        /// row-level decision made the outcome depend on their order.</para></summary>
        ZeroOnNewProduct,
        /// <summary>The row named a product by id and that product is gone — merged or deleted while the
        /// review grid sat open. Resolving it by name instead would quietly create a twin, or land the
        /// count on a different product than the dropdown showed; the grid said where this was going, so
        /// a row that can no longer go there is refused rather than redirected.</summary>
        ProductGone,
        /// <summary>The "how many" box was left empty. Not a zero (see <see cref="CensusRow.Count"/>) and
        /// not a guess — the one thing the household has to supply is the number.</summary>
        MissingCount,
        /// <summary>An unmatched row whose name is carried by MORE THAN ONE product. No unique index
        /// exists on product names, and <c>Attest</c> REPLACES a count — so resolving to the first
        /// twin would overwrite an arbitrary household number. The app declines to guess, the same
        /// refusal <c>MealStock</c> makes when a name cannot address a single product; the grid says
        /// so before the confirm and its dropdown tells the twins apart by their counts.</summary>
        AmbiguousName,
    }

    public record RefusedRow(string Name, CensusRefusal Reason);

    /// <param name="Counted">Products given a count by this census.</param>
    /// <param name="Rows">Rows that fed those products. Higher than <paramref name="Counted"/> whenever
    /// rows rolled up — which the reader's own contract makes routine, since it emits a row per variety
    /// but matches across varieties. Reported so the summary can explain the gap instead of looking like
    /// it dropped something.</param>
    /// <param name="NewProducts">Products this census introduced.</param>
    /// <param name="Retracked">Products it turned prediction back on for (<c>IsTracked</c>).</param>
    /// <param name="ResumedCounting">Products whose count was DORMANT and is now live again
    /// (<c>TrackQuantity</c>) — a different property from <paramref name="Retracked"/> and a different
    /// act. Stopping counting is deliberate and its stored number is a historical fact the app promises
    /// to keep; a census overwrites both, so it has to say so rather than let the one thing the household
    /// switched off be the one thing the summary doesn't mention.</param>
    /// <param name="AssertedOut">Products counted at zero — a human statement of an outage (§13.4), each
    /// of which wrote a real <c>OutNow</c>.</param>
    /// <param name="Refused">Rows that could not be recorded, each with its reason. Reported rather than
    /// swallowed: a row the household ticked and then didn't get is something they have to be told.</param>
    public record CensusOutcome(
        int Counted, int Rows, int NewProducts, int Retracked, int ResumedCounting, int AssertedOut,
        IReadOnlyList<RefusedRow> Refused);

    /// <summary>Record the reviewed rows as attested counts. Every row here is one a human ticked and
    /// checked — the reader only ever proposed them. The fate of each row is <see cref="CensusPlan"/>'s
    /// call; this method carries it out.</summary>
    public async Task<CensusOutcome> ConfirmAsync(
        IReadOnlyList<CensusRow> rows, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var products = await db.Products.ToListAsync(cancellationToken);
        var catalog = new CatalogIndex(products);

        // The plan reasons over the pure projection of each row. The read-time facts a grid supplies
        // (evidence/confidence/similarity/suggestion) only ever affect whether a row arrives TICKED, which a
        // write path never renders — so the neutral defaults are exactly right here.
        var states = rows.Select(r => new CensusPlan.CensusRowState(r.Name, r.Count, r.ProductId, r.CreateNew)).ToList();
        var plans = CensusPlan.Plan(states, catalog);

        // ⚠️ Counts are SUMMED per TARGET before a single Attest, not attested row by row. An attestation
        // states a TOTAL, so two rows resolving to one product (the same food in two photos, or two
        // varieties of it) would otherwise have the second silently overwrite the first. Existing products
        // key by identity (their instance); new products key by ProductMatcher's IDENTITY, so a name
        // transcribed "Home-Canned Sauce" one row and "Home Canned Sauce" the next still makes one product.
        var totals = new Dictionary<Product, decimal>();
        var createdByKey = new Dictionary<string, Product>();
        var retracked = new HashSet<Product>();
        var resumed = new HashSet<Product>();
        var refused = new List<RefusedRow>();
        int rowsLanded = 0, created = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var plan = plans[i];
            var name = row.Name.Trim();

            switch (plan.Action)
            {
                case CensusPlan.CensusAction.Refuse:
                    // A matched row can have its name box blanked (it resolves by id), so name the refusal by
                    // the product when there's no typed name — "(unnamed)" among thirty rows names nothing.
                    var label = name.Length > 0 ? name : catalog.ById(row.ProductId)?.Name ?? "(unnamed)";
                    refused.Add(new RefusedRow(label, MapRefusal(plan.Reason)));
                    break;

                case CensusPlan.CensusAction.LandOnProduct:
                {
                    // Planned against this same catalog, so the id is present.
                    var product = catalog.ById(plan.LandsOn!.Value)!;
                    MarkTrackAndResume(product, retracked, resumed);
                    // ⚠️ .Value, not `?? 0m`: the plan refuses a null count (MissingCount) before any Land or
                    // Create, so the count is real here — and coalescing a null to 0m would file a real OutNow
                    // (§13.4), the "empty box becomes an asserted out" harm the CensusRow.Count doc calls the
                    // sharpest edge on this page. If that invariant ever broke, .Value fails LOUD into the
                    // single transaction's rollback (the page's catch) rather than silently attesting a zero.
                    totals[product] = totals.GetValueOrDefault(product) + row.Count!.Value;
                    rowsLanded++;
                    break;
                }

                case CensusPlan.CensusAction.CreateProduct:
                {
                    var key = ProductMatcher.IdentityKey(name);
                    if (!createdByKey.TryGetValue(key, out var product))
                    {
                        // CreatedByReceiptId stays null: no receipt introduced this product, and claiming
                        // one would offer it up to that receipt's removal. The Category comes from a circuit
                        // <select>, so IsDefined-guard it — Enum.TryParse succeeds on a numeric string, and a
                        // tampered message must not persist an undefined enum (same guard SetCategory and
                        // create_product carry; default to Other rather than drop the whole row).
                        var category = Enum.IsDefined(row.Category) ? row.Category : Category.Other;
                        product = new Product { Name = name, Category = category };
                        db.Products.Add(product);
                        createdByKey[key] = product;
                        created++;
                    }
                    MarkTrackAndResume(product, retracked, resumed);
                    // .Value, not `?? 0m` — see the LandOnProduct case above; the plan guarantees non-null.
                    totals[product] = totals.GetValueOrDefault(product) + row.Count!.Value;
                    rowsLanded++;
                    break;
                }
            }
        }

        var now = DateTimeOffset.Now;
        var assertedOut = 0;
        foreach (var (product, total) in totals)
        {
            // Attest, not Add: this is a LOOK at the shelf, so it states the total and re-anchors the
            // attestation clock §13.5's drift check measures from. It also opts the product into counting.
            if (!StockLedger.Attest(product, total, now)) continue;

            // §13.4: a human's zero is real evidence and owes an OutNow, which feeds the burn-rate rhythm.
            // Only a human can get here — the reader floors its proposals at 1, so this zero was typed.
            db.InventorySignals.Add(new InventorySignal { Product = product, Kind = SignalKind.OutNow, SignaledAt = now });
            assertedOut++;
        }

        // History-only record, staged on the census's single-SaveChanges transaction. Only when the census
        // actually counted something — an all-refused census recorded no counts and has nothing to log.
        // NotReversible: a census attests counts, creates products, and files OutNow signals across many
        // rows at once, and v1 doesn't unpick that.
        if (totals.Count > 0)
            activityLog.Record(db, ActivityKind.CensusConfirmed, new CensusConfirmedPayload(totals.Count, created));

        await db.SaveChangesAsync(cancellationToken);
        await activityLog.TrimAsync(cancellationToken);
        return new CensusOutcome(
            Counted: totals.Count,
            Rows: rowsLanded,
            NewProducts: created,
            Retracked: retracked.Count,
            ResumedCounting: resumed.Count,
            AssertedOut: assertedOut,
            Refused: refused);
    }

    /// <summary>Note that a count is about to resume for a dormant product, and re-track an ignored one —
    /// both checked BEFORE the later <c>Attest</c> flips those flags, and both idempotent so two rows
    /// resolving to one product count each act once.</summary>
    private static void MarkTrackAndResume(Product product, HashSet<Product> retracked, HashSet<Product> resumed)
    {
        // A count that was stopped is DORMANT, not gone: its number and date stay true as history, and Attest
        // is about to overwrite both and start believing them again. That is the right thing for someone who
        // just counted the shelf — but it is the one switch they deliberately turned off, so it gets said.
        if (!product.TrackQuantity && product.QuantityOnHand is not null) resumed.Add(product);

        // Counting an item is a deliberate statement of interest, so — like buying it again — it ends the
        // grocery list's "ignore this for a while". Without this an untracked product would take a count no
        // prediction, list, or dashboard ever reads.
        if (!product.IsTracked)
        {
            product.IsTracked = true;
            retracked.Add(product);
        }
    }

    private static CensusRefusal MapRefusal(CensusPlan.CensusReason reason) => reason switch
    {
        CensusPlan.CensusReason.ProductGone => CensusRefusal.ProductGone,
        CensusPlan.CensusReason.MissingCount => CensusRefusal.MissingCount,
        CensusPlan.CensusReason.AmbiguousName => CensusRefusal.AmbiguousName,
        CensusPlan.CensusReason.NameTaken => CensusRefusal.DuplicateName,
        CensusPlan.CensusReason.ZeroOnNewProduct => CensusRefusal.ZeroOnNewProduct,
        // NegativeCount and NoName both mean "nothing usable here"; every other reason is not a refusal.
        _ => CensusRefusal.Unusable,
    };
}
