using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;

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
        /// <para>⚠️ Scoped to creation, and that scope was got wrong twice. Refusing every zero on a
        /// rhythm-less product (the wider rule) declined the COUNT along with the outage, which left
        /// §13.8's whole population — stock no receipt knows about — unable to be corrected to zero from
        /// the one surface that stands at the shelf, with the stale positive still telling recipes the
        /// food was there. The right split is not row-level at all: a zero is recorded as a number
        /// always, and the <c>OutNow</c> it would owe is withheld where no rhythm exists to contradict
        /// it. See the Attest loop.</para></summary>
        ZeroOnNewProduct,
        /// <summary>The row named a product by id and that product is gone — merged or deleted while the
        /// review grid sat open. Resolving it by name instead would quietly create a twin, or land the
        /// count on a different product than the dropdown showed; the grid said where this was going, so
        /// a row that can no longer go there is refused rather than redirected.</summary>
        ProductGone,
        /// <summary>The "how many" box was left empty. Not a zero (see <see cref="CensusRow.Count"/>) and
        /// not a guess — the one thing the household has to supply is the number.</summary>
        MissingCount,
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
        // A HashSet and not a counter: unlike IsTracked below, TrackQuantity is not flipped until Attest
        // runs in the second loop, so two rows resolving to one dormant product would both count it.
        var resumed = new HashSet<Product>();
        var refused = new List<RefusedRow>();
        // Zero rows that no product exists for YET. Held until every row has been read, because a later
        // row naming the same new item settles them (see where they are added).
        var deferredZeros = new List<(string Name, string Label)>();
        int created = 0, counted = 0;

        foreach (var row in rows)
        {
            var name = row.Name.Trim();

            // The id first — and a row that named a product which no longer exists is REFUSED, not
            // redirected. Resolving it by name instead would land the count on a different product than
            // the dropdown showed, or create a twin of the one that just went away; a merge or a delete
            // in another tab is all it takes. The grid said where this row was going, so a row that can
            // no longer go there is the household's business, not something to quietly re-decide.
            var product = row.ProductId > 0
                ? products.FirstOrDefault(p => p.Id == row.ProductId)
                : null;

            if (row.ProductId > 0 && product is null)
            {
                refused.Add(new RefusedRow(name.Length > 0 ? name : "(unnamed)", CensusRefusal.ProductGone));
                continue;
            }

            // A row matched to a product resolves BY that product, so blanking its name box (to retype it,
            // say) must not drop it — but every refusal below still has to be able to name the row, and
            // "(unnamed)" among thirty rows is not a name.
            var label = name.Length > 0 ? name : product?.Name ?? "(unnamed)";

            // ⚠️ An EMPTY box is not a zero, and coercing it to one is the sharpest edge on this page:
            // an attested zero writes a real OutNow (§13.4), so a box cleared to retype would become a
            // household statement that they had run out, pinning the item Overdue. Same rule as the
            // product page's count panel, for the same reason.
            if (row.Count is not { } count)
            {
                refused.Add(new RefusedRow(label, CensusRefusal.MissingCount));
                continue;
            }

            // A negative count is REFUSED, never clamped. StockLedger.Attest floors at zero, and a floored
            // "-3" would land on zero — which is an ASSERTED out, writing a real OutNow into the cadence
            // engine off a typo. Same rule, same reason, as EfPantryStore.SetQuantityAsync's refusal.
            if (count < 0)
            {
                refused.Add(new RefusedRow(label, CensusRefusal.Unusable));
                continue;
            }

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
                    // A near-miss is the GRID's business, not this method's: resolving a fuzzy match here
                    // would attach a count to a guessed product with nobody asked.
                    if (row.CreateNew)
                    {
                        refused.Add(new RefusedRow(name, CensusRefusal.DuplicateName));
                        continue;
                    }
                    product = sameName;
                }
            }

            // ⚠️ A zero cannot bring a product into existence — but whether it WOULD is not knowable yet,
            // so this row is set aside and settled once every row has been read. Deciding it here made
            // the outcome depend on row ORDER: [Sardines 0, Sardines 2] refused the first row while the
            // second created Sardines, then told the household "nothing was created" about a product
            // sitting on their Products page, where [Sardines 2, Sardines 0] refused nothing. The reader
            // emits a row per variety and matches across varieties, so two rows naming one new item is
            // its ordinary output, not an edge case.
            if (count == 0 && product is null)
            {
                deferredZeros.Add((name, label));
                continue;
            }

            if (product is null)
            {
                // CreatedByReceiptId stays null: no receipt introduced this product, and claiming one
                // would offer it up to that receipt's removal.
                product = new Product { Name = name, Category = row.Category };
                db.Products.Add(product);
                products.Add(product); // later rows in this census can resolve to it
                createdByName[name] = product;
                created++;
            }

            // A count that was stopped is DORMANT, not gone: its number and date stay true as history, and
            // Attest is about to overwrite both and start believing them again. That is the right thing to
            // do for someone who just counted the shelf — but it is the one switch they deliberately
            // turned off, so it gets said out loud rather than being the only change the summary omits.
            if (!product.TrackQuantity && product.QuantityOnHand is not null)
            {
                resumed.Add(product);
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

            totals[product] = totals.GetValueOrDefault(product) + count;
            counted++;
        }

        // Settle the zero rows now that the whole census has been read, so the answer is a property of
        // the rows rather than of their order. A sibling row naming the same item makes this one part of
        // the same statement — it contributes its zero and counts as a row that landed; with nothing to
        // attach to it is refused, which is the only zero that genuinely has nothing to record.
        foreach (var (name, label) in deferredZeros)
        {
            if (createdByName.TryGetValue(name, out var product))
            {
                totals[product] = totals.GetValueOrDefault(product);
                counted++;
            }
            else
            {
                refused.Add(new RefusedRow(label, CensusRefusal.ZeroOnNewProduct));
            }
        }

        var now = DateTimeOffset.Now;
        var assertedOut = 0;
        foreach (var (product, count) in totals)
        {
            // Attest, not Add: this is a LOOK at the shelf, so it states the total and re-anchors the
            // attestation clock §13.5's drift check measures from. It also opts the product into counting
            // (the ledger's rule: typing a number IS asking for it to be counted).
            if (!StockLedger.Attest(product, count, now)) continue;

            // §13.4: a human's zero is real evidence and owes an OutNow, which feeds the burn-rate
            // rhythm. Only a human can get here — the reader floors its proposals at 1, so this zero
            // was typed. Unconditional, and see StockLedger.Attest for why an attempt to withhold it for
            // a product with no purchase history was measured and reverted.
            db.InventorySignals.Add(new InventorySignal
            {
                Product = product,
                Kind = SignalKind.OutNow,
                SignaledAt = now,
            });
            assertedOut++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new CensusOutcome(
            Counted: totals.Count,
            Rows: counted,
            NewProducts: created,
            Retracked: retracked.Count,
            ResumedCounting: resumed.Count,
            AssertedOut: assertedOut,
            Refused: refused);
    }
}
