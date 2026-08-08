using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Census;

/// <summary>
/// The ONE decision core for a reviewed shelf census (DESIGN.md §13.8), consumed by BOTH the review grid
/// (<c>PantryPhoto.razor</c>) and the write path (<c>CensusConfirmationService</c>). It answers, in one
/// place and one pass over the whole census, the five questions the grid used to answer in five different
/// places from five different subsets of inputs — arrive ticked? tick-all eligible? what will confirm do?
/// why isn't it ticked? what does confirm actually write? — which is why inserting a branch at the top of
/// the markup kept breaking a neighbour.
/// <para>Two pure entry points, no EF and no rendering, so the whole combinatorial space is unit-testable:
/// <see cref="Prefill"/> decides what the dropdown pre-selects at read time, and <see cref="Plan"/> decides
/// every row's fate given the current edits. The grid supplies the read-time facts (evidence, confidence,
/// a still-selected fuzzy match, an ambiguous suggestion) and reads all four plan fields; the service
/// supplies neutral read-time facts and reads only <see cref="CensusRowPlan.Action"/> /
/// <see cref="CensusRowPlan.LandsOn"/> / <see cref="CensusRowPlan.Reason"/> — because the WRITE decision
/// depends only on the name, the count, and the dropdown, never on how confidently the reader saw it.</para>
/// <para>Precedent in-repo: <c>ReportSpecRules</c>, one rules class the builder UI and <c>ReportEngine.Run</c>
/// both consult so a spec can't be legal on screen and rejected by the engine.</para>
/// </summary>
public static class CensusPlan
{
    /// <summary>At or above this the reader wasn't guessing, so the row is ticked for you — the SAME 0.6 the
    /// receipt review grid highlights a low-confidence line at, kept identical so there is one number in the
    /// app for "the model was guessing".</summary>
    public const decimal ConfidentEnough = 0.6m;

    /// <summary>What the confirm will do with a row.</summary>
    public enum CensusAction
    {
        /// <summary>Attest the count onto an existing product (<see cref="CensusRowPlan.LandsOn"/>).</summary>
        LandOnProduct,
        /// <summary>Create a new product from the row's name and attest the count onto it. Rows that create
        /// the SAME item — same <see cref="ProductMatcher.IdentityKey"/> — become one product, counts summed.</summary>
        CreateProduct,
        /// <summary>Record nothing. <see cref="CensusRowPlan.Reason"/> says why, in words fit for the row the
        /// household ticked and then didn't get.</summary>
        Refuse,
    }

    /// <summary>One value per row, computed in priority order in ONE place — so the grid's "why" message is a
    /// <c>switch</c> on this, one message per row by construction, and no ordering bug is expressible.
    /// <para>Only the message-bearing and refusal states appear here; a clean landing or creation carries no
    /// "why" (the evidence chip and the dropdown say it), and an unidentified row's "name it" note and its
    /// withheld tick both ride on <see cref="CensusItem.Evidence"/> directly rather than a reason value.</para></summary>
    public enum CensusReason
    {
        /// <summary>Attests an existing product the dropdown names outright. No message.</summary>
        LandsOnProduct,
        /// <summary>Creates a new product from a novel name. No message.</summary>
        CreatesProduct,
        /// <summary>The pre-filled match came from name SIMILARITY and is still selected — an unscored guess at
        /// WHICH product, whose count an attest would replace. Ticks only when a human confirms it.</summary>
        MatchedBySimilarity,
        /// <summary>A typed name merely RESEMBLES an existing product (fuzzy, not an identity). The row will
        /// create a separate item unless the human picks the existing one — named, never resolved, because
        /// fuzzy matching false-positives.</summary>
        ResemblesExisting,
        /// <summary>The reader's suggestion named a product TWO products answer to, so it could not be honored
        /// and nothing was pre-filled. A read-time fact about a DIFFERENT string than the row's name, which is
        /// why it is carried on the row rather than re-derived.</summary>
        AmbiguousSuggestion,
        /// <summary>The dropdown says create-new but the typed name is exactly an existing product's, so the
        /// count will land THERE — said so the screen and the write can't disagree about where it goes.</summary>
        WillLandOnExisting,
        /// <summary>The row's own name is one MORE THAN ONE product answers to. Refused, not guessed: an attest
        /// replaces a count, and no unique index exists on names, so the household picks which twin.</summary>
        AmbiguousName,
        /// <summary>An explicit "create new" whose name is already taken. Creating the twin would split
        /// history; silently merging would overrule the human — so it is declined and named.</summary>
        NameTaken,
        /// <summary>The "how many" box was left empty. Not a zero (that would file a real outage), and not a
        /// guess — the one thing the household must supply.</summary>
        MissingCount,
        /// <summary>A negative count. Refused, never clamped: a floored "-3" lands on zero, which is an
        /// ASSERTED out writing a real OutNow off a typo.</summary>
        NegativeCount,
        /// <summary>A count of zero on a row that would CREATE the product — a phantom the household has never
        /// owned. Scoped to creation and nothing wider (every other zero records its outage). Decided AFTER
        /// the whole census is read: a sibling row naming the same new item settles it.</summary>
        ZeroOnNewProduct,
        /// <summary>The dropdown named a product id that is gone — merged or deleted while the grid sat open.
        /// Refused rather than redirected onto a different product than the dropdown showed.</summary>
        ProductGone,
        /// <summary>No name to resolve by, and no product selected either — nothing to record.</summary>
        NoName,
    }

    /// <summary>The fate of one census row.</summary>
    /// <param name="Action">What the confirm does.</param>
    /// <param name="LandsOn">The existing product id a <see cref="CensusAction.LandOnProduct"/> attests onto;
    /// null for a create or a refusal.</param>
    /// <param name="Reason">The single classification driving the grid message and the refusal wording.</param>
    /// <param name="NeedsAHumanLook">Withhold the tick — the non-confidence half of the tick rule. The arrival
    /// default is <c>!NeedsAHumanLook &amp;&amp; confidence &gt;= 0.6</c>; Tick all is <c>!NeedsAHumanLook</c>
    /// (the SAME value, not a re-derivation). True for a guess, an ambiguity, an unidentified package, or
    /// anything the confirm would refuse.</param>
    public readonly record struct CensusRowPlan(CensusAction Action, int? LandsOn, CensusReason Reason, bool NeedsAHumanLook);

    /// <summary>The pure projection of a review row the plan reasons over. The grid fills the read-time facts
    /// from the reader and the live dropdown; the service fills neutral values for the four it doesn't have
    /// (they only ever affect <see cref="CensusRowPlan.NeedsAHumanLook"/>, which the service ignores).</summary>
    /// <param name="Name">The current (possibly edited) item name.</param>
    /// <param name="Count">What the human says is there, or null when the box is empty (never coerced to zero).</param>
    /// <param name="ChosenProductId">The dropdown's current value; 0 = create new.</param>
    /// <param name="ChoseCreateNew">The human deliberately turned a matched row into a new product — told apart
    /// from "never matched" (both are <see cref="ChosenProductId"/> 0) because the two are refused differently.</param>
    /// <param name="SimilaritySelected">The chosen product is a still-selected fuzzy pre-fill (grid only).</param>
    /// <param name="AmbiguousSuggestion">The reader's suggestion named a product more than one answers to, and
    /// it isn't the row's own name (grid only; null once the human picks anything).</param>
    /// <param name="Evidence">How the reader knew the item (grid only). Unidentified withholds the tick and
    /// earns the "name it" note wherever the row lands.</param>
    /// <param name="Confidence">Certainty in the identification, 0–1 (grid only; drives the arrival tick).</param>
    public readonly record struct CensusRowState(
        string Name, decimal? Count, int ChosenProductId, bool ChoseCreateNew,
        bool SimilaritySelected = false, string? AmbiguousSuggestion = null,
        CensusEvidence Evidence = CensusEvidence.Label, decimal Confidence = 1m);

    /// <summary>What the dropdown pre-selects for a freshly read item, and why. Replaces the grid's read-time
    /// match step.</summary>
    /// <param name="ProductId">The existing product to pre-select, or 0 for create-new (no confident single match).</param>
    /// <param name="BySimilarity">The pre-fill came from fuzzy similarity, not a label/suggestion/identity match —
    /// an unscored guess at WHICH product, which keeps the row off the auto-tick list and says so.</param>
    /// <param name="AmbiguousSuggestion">The reader's suggestion named a product more than one answers to (and it
    /// isn't the row's own name), so it could not be honored — carried so the row can say what it read this as.</param>
    public readonly record struct CensusPrefill(int ProductId, bool BySimilarity, string? AmbiguousSuggestion);

    /// <summary>Decide the dropdown pre-fill for a freshly read item, in the census's trust order: an
    /// unidentified package is never matched (it names a container); the model's own suggestion leads, resolved
    /// by the matcher's rule-1 identity set (which folds a punctuation variant onto the one product it names,
    /// and refuses to pick between twins); then the deterministic matcher backs it up.</summary>
    public static CensusPrefill Prefill(CensusItem item, CatalogIndex catalog)
    {
        // An unidentified package names a CONTAINER, so matching it against the catalog would attach a count to
        // a product on no evidence. The reader already refuses to suggest one; this holds the same rule on the
        // fuzzy fallback, which would happily match "unlabeled tub" to a tub of anything.
        if (item.Evidence == CensusEvidence.Unidentified) return new(0, false, null);

        if (item.SuggestedProductName is { Length: > 0 } suggested)
        {
            // ⚠️ Plural on purpose: two products can share a name (no unique index), the reader's hint list is
            // Distinct(), so its suggestion names a NAME — and an attest replaces a count, so First() here would
            // pre-authorize a write over an arbitrary twin. The identity set is the MATCHER's, the same
            // definition every guard and the confirm's refusal use.
            var exact = catalog.ExactMatches(suggested);
            if (exact.Count > 1)
            {
                // The name rides along only when the live guards can't already see it — i.e. when it isn't the
                // row's own name. When it IS, AmbiguousName states it live (and keeps stating it as the human
                // edits), so carrying it too would put two sentences on one row.
                var hidden = ProductMatcher.IdentityKey(suggested) != ProductMatcher.IdentityKey(item.NormalizedName);
                return new(0, false, hidden ? suggested : null);
            }
            if (exact.Count == 1) return new(exact[0].Id, false, null);
        }

        // Ask the matcher WHICH rule fired rather than re-deriving it from the strings: it normalizes
        // punctuation away before its exact rule, so a raw compare would call a rule-1 identity a guess.
        var (resolved, kind) = catalog.ResolveWithKind(item.NormalizedName);
        if (resolved is not null && kind == ProductMatcher.MatchKind.ExactName
            && catalog.ExactMatches(item.NormalizedName).Count > 1)
        {
            // Rule 1 found an identity that isn't one product — the ambiguous name IS the row's own, so it
            // pre-fills nothing and AmbiguousName states it live.
            return new(0, false, null);
        }
        var bySimilarity = kind is ProductMatcher.MatchKind.Substring or ProductMatcher.MatchKind.TokenOverlap;
        return new(resolved?.Id ?? 0, bySimilarity, null);
    }

    /// <summary>Plan every row's fate in ONE whole-census pass. Whole-census, not per-row, because the answer
    /// to "does this zero create a product?" is a property of the census, not of the order the reader emitted
    /// rows in: a count-of-zero on a novel name is set aside and settled only once every sibling has been seen,
    /// so <c>[Sardines 0, Sardines 2]</c> and <c>[Sardines 2, Sardines 0]</c> read identically.</summary>
    public static IReadOnlyList<CensusRowPlan> Plan(IReadOnlyList<CensusRowState> rows, CatalogIndex catalog)
    {
        var prelim = new CensusRowPlan[rows.Count];
        var createKey = new string?[rows.Count];   // the identity key a create row would create under, else null
        var deferredZero = new bool[rows.Count];    // a zero-count create row whose fate waits on its siblings
        // Which identity keys WILL be created by a positive (count > 0) create row this census. A zero row
        // whose key is here joins that product; a zero row whose key is NOT here has nothing to attach to.
        var positiveCreateKeys = new HashSet<string>();

        for (var i = 0; i < rows.Count; i++)
        {
            (prelim[i], createKey[i], deferredZero[i]) = Classify(rows[i], catalog);
            if (createKey[i] is { } key && !deferredZero[i]) positiveCreateKeys.Add(key);
        }

        var result = new CensusRowPlan[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            result[i] = deferredZero[i] && !positiveCreateKeys.Contains(createKey[i]!)
                // A zero with no sibling to bring the product into existence: nothing to add, nothing to count.
                ? prelim[i] with { Action = CensusAction.Refuse, LandsOn = null, Reason = CensusReason.ZeroOnNewProduct, NeedsAHumanLook = true }
                // Either a real count, or a zero a sibling creates (it contributes its zero to that product).
                : prelim[i];
        }
        return result;
    }

    private static (CensusRowPlan Plan, string? CreateKey, bool DeferredZero) Classify(CensusRowState state, CatalogIndex catalog)
    {
        var name = state.Name.Trim();
        var chosen = state.ChosenProductId;
        var count = state.Count;
        // An unidentified package earns a look wherever it lands — its "name it" note and its withheld tick
        // both ride on the evidence, not on the reason, so a row named into an ordinary match still says so.
        var evidenceLook = state.Evidence == CensusEvidence.Unidentified;

        (CensusRowPlan, string?, bool) Refuse(CensusReason r) => (new(CensusAction.Refuse, null, r, true), null, false);
        (CensusRowPlan, string?, bool) Land(int id, CensusReason r, bool look) =>
            (new(CensusAction.LandOnProduct, id, r, look || evidenceLook), null, false);

        // The dropdown names a product outright.
        if (chosen > 0)
        {
            if (catalog.ById(chosen) is null) return Refuse(CensusReason.ProductGone);
            if (count is null) return Refuse(CensusReason.MissingCount);
            if (count < 0) return Refuse(CensusReason.NegativeCount);
            return state.SimilaritySelected
                ? Land(chosen, CensusReason.MatchedBySimilarity, look: true)
                : Land(chosen, CensusReason.LandsOnProduct, look: false);
        }

        // Create-new dropdown (chosen == 0): the count and name gates come before name resolution, the same
        // order the confirm has always used.
        if (count is null) return Refuse(CensusReason.MissingCount);
        if (count < 0) return Refuse(CensusReason.NegativeCount);
        if (name.Length == 0) return Refuse(CensusReason.NoName);

        var identity = catalog.ExactMatches(name);
        if (state.ChoseCreateNew)
        {
            // An explicit create-new whose name is taken is the household's own choice colliding with reality —
            // declined and named, never silently merged onto the existing product's count.
            if (identity.Count >= 1) return Refuse(CensusReason.NameTaken);
        }
        else
        {
            if (identity.Count > 1) return Refuse(CensusReason.AmbiguousName);
            // An unmatched name that IS an existing product resolves to it (the retry-safety rule) — said, so
            // the screen and the write agree about where the count lands.
            // ⚠️ Withhold the tick when the reader ALSO handed us an ambiguous suggestion: the row's own name
            // identity-matches ONE product, but the reader thought it was a twin — a conflict the household
            // must resolve. Auto-ticking here (the old look:false) silently attested over the name-matched
            // product's count while the twin the reader saw got nothing. This branch returns before
            // CreateReason, so the suggestion is consulted right here or not at all.
            if (identity.Count == 1)
                return Land(identity[0].Id, CensusReason.WillLandOnExisting, look: state.AmbiguousSuggestion is not null);
        }

        // A genuinely novel name: create. The reason turns on what the reader or the typed name suggests, but
        // the ACTION is the same — which is why the service can ignore it. Only an ambiguous SUGGESTION withholds
        // the tick: a fuzzy resemblance (ResemblesExisting) is a soft heads-up, not a guess about WHICH product
        // gets overwritten — the row creates a separate item, the human has already typed the name, and fuzzy
        // matching false-positives, so bulk-ticking it to create is a valid flow, exactly as before.
        var reason = CreateReason(state, name, catalog);
        var needsLook = reason is CensusReason.AmbiguousSuggestion;
        var plan = new CensusRowPlan(CensusAction.CreateProduct, null, reason, needsLook || evidenceLook);
        return (plan, ProductMatcher.IdentityKey(name), count == 0m);
    }

    private static CensusReason CreateReason(CensusRowState state, string name, CatalogIndex catalog)
    {
        // An unresolved ambiguous suggestion speaks first — it names what the reader thought this was, which is
        // more precise than what the typed name resembles, and it is a read-time fact the live guards can't see.
        if (state.AmbiguousSuggestion is not null) return CensusReason.AmbiguousSuggestion;
        // The standing duplicate guard where a name is TYPED rather than read: a fuzzy resemblance is named (the
        // household decides) but never resolved here — resolving would attach a count to a guessed product.
        var (resolved, kind) = catalog.ResolveWithKind(name);
        if (resolved is not null && kind is ProductMatcher.MatchKind.Substring or ProductMatcher.MatchKind.TokenOverlap)
            return CensusReason.ResemblesExisting;
        return CensusReason.CreatesProduct;
    }
}
