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
    /// <param name="ProductId">The product this row resolved to, or 0 for none.</param>
    /// <param name="CreateNew">⚠️ Whether the HUMAN explicitly chose "create a new product" for this row,
    /// as opposed to the grid simply never having matched it. Both arrive as <paramref name="ProductId"/>
    /// 0, and collapsing them is a real bug this once had: the name fallback below would then resolve an
    /// explicit create-new onto the existing product of the same name and REPLACE its count — the screen
    /// saying "new product" while the write destroyed an old one.</param>
    /// <param name="Count">What the human says is there. Decimal because a weight item's count is
    /// fractional in its own unit (§13.1) — the reader can only ever propose a whole number, but the
    /// person editing the row can type 2.5.</param>
    public record CensusRow(string Name, Category Category, decimal Count, int ProductId, bool CreateNew = false);

    /// <summary>Why a row could not be recorded. An enum rather than a bare count because the three cases
    /// need genuinely different sentences — one is a typo, one is a name clash the household can resolve,
    /// and one is a claim the app declines to make on their behalf.</summary>
    public enum CensusRefusal
    {
        /// <summary>Nothing to record: a negative count, or no name to resolve by.</summary>
        Unusable,
        /// <summary>An explicit "create new" whose name is already taken. Creating the twin would split
        /// purchase history and blind the predictor; silently merging would overrule the human's choice.
        /// So the row is declined and named, and they pick which they meant.</summary>
        DuplicateName,
        /// <summary>A count of zero on a product that does not exist yet. An attested zero writes a real
        /// <c>OutNow</c> (§13.4), and "we have run out of a thing we have never owned" is not evidence of
        /// anything — it would pin a brand-new product Overdue at the top of the grocery list forever.</summary>
        OutageOnNewProduct,
    }

    public record RefusedRow(string Name, CensusRefusal Reason);

    /// <param name="Counted">Products given a count by this census.</param>
    /// <param name="Rows">Rows that fed those products. Higher than <paramref name="Counted"/> whenever
    /// rows rolled up — which the reader's own contract makes routine, since it emits a row per variety
    /// but matches across varieties. Reported so the summary can explain the gap instead of looking like
    /// it dropped something.</param>
    /// <param name="NewProducts">Products this census introduced.</param>
    /// <param name="Retracked">Products it turned prediction back on for.</param>
    /// <param name="AssertedOut">Products counted at zero — a human statement of an outage (§13.4), each
    /// of which wrote a real <c>OutNow</c>.</param>
    /// <param name="Refused">Rows that could not be recorded, each with its reason. Reported rather than
    /// swallowed: a row the household ticked and then didn't get is something they have to be told.</param>
    public record CensusOutcome(
        int Counted, int Rows, int NewProducts, int Retracked, int AssertedOut, IReadOnlyList<RefusedRow> Refused);

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
        var refused = new List<RefusedRow>();
        int created = 0, counted = 0;

        foreach (var row in rows)
        {
            var name = row.Name.Trim();
            // A negative count is REFUSED, never clamped. StockLedger.Attest floors at zero, and a floored
            // "-3" would land on zero — which is an ASSERTED out, writing a real OutNow into the cadence
            // engine off a typo. Same rule, same reason, as EfPantryStore.SetQuantityAsync's refusal.
            if (row.Count < 0)
            {
                refused.Add(new RefusedRow(name.Length > 0 ? name : "(unnamed)", CensusRefusal.Unusable));
                continue;
            }

            // The id first, and a name is required only from here on — a row matched to a product resolves
            // by that product, so blanking its name box (to retype it, say) must not silently drop it.
            var product = row.ProductId > 0
                ? products.FirstOrDefault(p => p.Id == row.ProductId)
                : null;

            if (product is null)
            {
                if (name.Length == 0)
                {
                    refused.Add(new RefusedRow("(unnamed)", CensusRefusal.Unusable));
                    continue;
                }

                // One census's photos can name a single new item on two rows — both map to one new product.
                if (createdByName.TryGetValue(name, out var existingNew))
                {
                    product = existingNew;
                }
                else if (products.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } sameName)
                {
                    // ⚠️ Only for a row the grid never matched. An UNMATCHED row whose name is exactly an
                    // existing product's is that product: a census is the app's biggest bulk product
                    // creator, a twin splits purchase history and blinds the predictor, and this is also
                    // what makes a RETRY safe (a confirm that commits then fails on the way back invites a
                    // second press, which would otherwise duplicate every product the first one created).
                    // But when the human EXPLICITLY chose "create new", doing this overrules them and
                    // replaces the existing product's count — so that row is declined and named instead,
                    // and they say which of the two they meant. Creating the twin silently is the other
                    // wrong answer; the standing duplicate guard blocks exact-name dupes outright.
                    if (row.CreateNew)
                    {
                        refused.Add(new RefusedRow(name, CensusRefusal.DuplicateName));
                        continue;
                    }
                    product = sameName;
                }
                else if (row.Count == 0)
                {
                    // Nothing to attach to, so creating the product first would make this an attested zero
                    // on an item the household has never owned: a real OutNow, pinning a brand-new product
                    // Overdue at the top of the dashboard and the grocery list until they buy one. You
                    // cannot run out of something you have never had. A zero on an EXISTING product is
                    // untouched by this — that one is §13.4's real evidence.
                    refused.Add(new RefusedRow(name, CensusRefusal.OutageOnNewProduct));
                    continue;
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
            counted++;
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
        return new CensusOutcome(totals.Count, counted, created, retracked.Count, assertedOut, refused);
    }
}
