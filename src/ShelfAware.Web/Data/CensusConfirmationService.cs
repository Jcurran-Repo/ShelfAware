using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Data;

/// <summary>
/// The ONE path that turns a reviewed shelf photo into counts (DESIGN.md §13.8) — and deliberately NOT
/// <see cref="ReceiptConfirmationService"/>, which is otherwise the app's one confirm path.
/// <para>★ <b>A census must never create a <see cref="PurchaseEvent"/>.</b> You did not buy the contents of
/// your freezer today; recording that you did would invent purchase dates, and every rhythm in the app is
/// learned from purchase dates. So a census writes exactly two things: products that did not exist yet, and
/// an attested count. The census reuses the receipt review GRID, and that similarity is precisely why this
/// has its own service — "reuses the extraction shape end to end" is true of the line contract and false of
/// the writing.</para>
/// <para>One SaveChanges, one transaction: a failure persists nothing, so a half-recorded census can't
/// leave the household with counts for the top of the shelf and none for the bottom.</para>
/// </summary>
public class CensusConfirmationService(IHouseholdDbFactory dbFactory)
{
    /// <param name="ProductId">Resolved product id; 0 means "create a new product" from this row.</param>
    /// <param name="Count">What the human says is there. Decimal because a weight item's count is
    /// fractional in its own unit (§13.1) — the reader can only ever propose a whole number, but the
    /// person editing the row can type 2.5.</param>
    public record CensusRow(string Name, Category Category, decimal Count, int ProductId);

    /// <param name="Counted">Products given a count by this census.</param>
    /// <param name="NewProducts">Products this census introduced.</param>
    /// <param name="Retracked">Products it turned prediction back on for.</param>
    /// <param name="AssertedOut">Products counted at zero — a human statement of an outage (§13.4), each
    /// of which wrote a real <c>OutNow</c>.</param>
    /// <param name="Refused">Rows that could not be recorded (blank name, negative count). Reported rather
    /// than swallowed: a row the household ticked and then didn't get is something they have to be told.</param>
    public record CensusOutcome(int Counted, int NewProducts, int Retracked, int AssertedOut, int Refused);

    /// <summary>Record the reviewed rows as attested counts. Every row here is one a human ticked and
    /// checked — the reader only ever proposed them.</summary>
    public async Task<CensusOutcome> ConfirmAsync(
        IReadOnlyList<CensusRow> rows, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var products = await db.Products.ToListAsync(cancellationToken);

        // One trip's photos can name a single new item on two rows — map both to one new product, keyed by
        // item name, exactly as the receipt confirm does.
        var createdByName = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        // ⚠️ Counts are SUMMED per product before a single Attest, not attested row by row. An attestation
        // states a TOTAL, so two rows resolving to one product (the same food in two photos, or two
        // varieties of it) would otherwise have the second silently overwrite the first — a household with
        // five would be left believing they had two, with nothing on screen saying so.
        var totals = new Dictionary<Product, decimal>();
        var retracked = new HashSet<Product>();
        int created = 0, refused = 0;

        foreach (var row in rows)
        {
            var name = row.Name.Trim();
            // A negative count is REFUSED, never clamped. StockLedger.Attest floors at zero, and a floored
            // "-3" would land on zero — which is an ASSERTED out, writing a real OutNow into the cadence
            // engine off a typo. Same rule, same reason, as EfPantryStore.SetQuantityAsync's refusal.
            if (name.Length == 0 || row.Count < 0)
            {
                refused++;
                continue;
            }

            Product product;
            if (row.ProductId > 0 && products.FirstOrDefault(p => p.Id == row.ProductId) is { } resolved)
            {
                product = resolved;
            }
            else if (createdByName.TryGetValue(name, out var existingNew))
            {
                product = existingNew;
            }
            // An unmatched row whose name is EXACTLY an existing product's is that product. The grid
            // pre-fills its dropdown through ProductMatcher, so this is the safety net under it — and it
            // earns its place twice over. A census is the app's biggest bulk product creator, and a twin
            // product splits purchase history and blinds the predictor (the standing duplicate-guard rule).
            // It is also what makes a RETRY safe: if a confirm commits and then fails on the way back, the
            // household's obvious move is to press it again, and without this that second press would file
            // a duplicate of every product the first one created.
            else if (products.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } sameName)
            {
                product = sameName;
            }
            else
            {
                // CreatedByReceiptId stays null: no receipt introduced this product, and claiming one
                // would offer it up to that receipt's removal.
                product = new Product { Name = name, Category = row.Category };
                db.Products.Add(product);
                products.Add(product); // later rows in this census can resolve to it
                createdByName[name] = product;
                created++;
            }

            // Counting an item is a deliberate statement of interest in it, so — like buying it again — it
            // ends the grocery list's "ignore this for a while". Without this an untracked product would
            // take a count that no prediction, list, or dashboard ever reads: a silent no-op on the one
            // row the household had just gone to the trouble of counting.
            if (!product.IsTracked)
            {
                product.IsTracked = true;
                retracked.Add(product);
            }

            totals[product] = totals.GetValueOrDefault(product) + row.Count;
        }

        var now = DateTimeOffset.Now;
        var assertedOut = 0;
        foreach (var (product, count) in totals)
        {
            // Attest, not Add: this is a LOOK at the shelf, so it states the total and re-anchors the
            // attestation clock §13.5's drift check measures from. It also opts the product into counting
            // (the ledger's rule: typing a number IS asking for it to be counted).
            if (StockLedger.Attest(product, count, now))
            {
                // §13.4: a human's zero is real evidence and owes an OutNow, which feeds the burn-rate
                // rhythm. Only a human can get here — the reader floors its proposals at 1, so this zero
                // was typed.
                db.InventorySignals.Add(new InventorySignal
                {
                    Product = product,
                    Kind = SignalKind.OutNow,
                    SignaledAt = now,
                });
                assertedOut++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new CensusOutcome(totals.Count, created, retracked.Count, assertedOut, refused);
    }
}
