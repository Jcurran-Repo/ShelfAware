using ShelfAware.Core.Census;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using Action = ShelfAware.Core.Census.CensusPlan.CensusAction;
using Reason = ShelfAware.Core.Census.CensusPlan.CensusReason;

namespace ShelfAware.Tests;

/// <summary>
/// The census decision core (DESIGN.md §13.8) as pure functions — no EF, no bUnit, so the whole
/// combinatorial space is reachable here. Every bug in the census arc was "that combination wasn't
/// considered", and this suite exists so a new one is a failing case rather than a shipped defect: the
/// grid and the write service both consume <see cref="CensusPlan"/>, so a rule proven here holds on both.
/// </summary>
public class CensusPlanTests
{
    private static Product P(int id, string name) => new() { Id = id, Name = name, Category = Category.Frozen };
    private static CatalogIndex Cat(params Product[] products) => new(products);

    private static CensusItem Read(string name, CensusEvidence ev = CensusEvidence.Label, string? suggested = null) =>
        new() { NormalizedName = name, Evidence = ev, SuggestedProductName = suggested };

    private static CensusPlan.CensusRowState Row(
        string name, decimal? count, int chosen = 0, bool createNew = false, bool similarity = false,
        string? ambiguousSuggestion = null, CensusEvidence evidence = CensusEvidence.Label, decimal confidence = 0.9m) =>
        new(name, count, chosen, createNew, similarity, ambiguousSuggestion, evidence, confidence);

    private static CensusPlan.CensusRowPlan Plan1(CensusPlan.CensusRowState state, CatalogIndex catalog) =>
        CensusPlan.Plan([state], catalog).Single();

    // The tick rule as the grid applies it — asserted here so its two halves (the plan's NeedsAHumanLook and
    // the confidence floor) are pinned in Core, not only in a page render.
    private static bool ArrivalTicks(CensusPlan.CensusRowPlan plan, decimal confidence) =>
        !plan.NeedsAHumanLook && confidence >= CensusPlan.ConfidentEnough;

    // ---- Prefill: what the dropdown pre-selects, and why -------------------------------------------

    [Fact]
    public void Prefill_never_matches_an_unidentified_package()
    {
        // Its name describes a container, so matching it would attach a count to a real product on no evidence.
        var catalog = Cat(P(1, "Freezer Bag"));
        var prefill = CensusPlan.Prefill(Read("frosted freezer bag", CensusEvidence.Unidentified), catalog);
        Assert.Equal(new CensusPlan.CensusPrefill(0, false, null), prefill);
    }

    [Fact]
    public void Prefill_takes_the_readers_suggestion_when_it_names_one_product()
    {
        var catalog = Cat(P(1, "Whole Milk"), P(2, "Half-and-Half"));
        var prefill = CensusPlan.Prefill(Read("Milk", suggested: "Whole Milk"), catalog);
        Assert.Equal(new CensusPlan.CensusPrefill(1, false, null), prefill);
    }

    [Fact]
    public void Prefill_folds_a_punctuation_variant_suggestion_onto_the_one_product()
    {
        var catalog = Cat(P(1, "Half-and-Half"));
        var prefill = CensusPlan.Prefill(Read("creamer", suggested: "Half and Half"), catalog);
        Assert.Equal(1, prefill.ProductId); // rule 1 identity, not a raw compare
    }

    [Fact]
    public void Prefill_refuses_to_pick_a_twin_from_an_ambiguous_suggestion_and_carries_the_name()
    {
        // The suggestion names a NAME two products answer to — First() would pre-authorize a write over an
        // arbitrary twin. Nothing pre-filled; the name rides along because the live guards judge the row's OWN
        // name (a different string) and cannot recover it.
        var catalog = Cat(P(1, "Home-Canned Tomato Sauce"), P(2, "Home Canned Tomato Sauce"));
        var prefill = CensusPlan.Prefill(Read("Canned Tomato Sauce", suggested: "Home Canned Tomato Sauce"), catalog);
        Assert.Equal(0, prefill.ProductId);
        Assert.False(prefill.BySimilarity);
        Assert.Equal("Home Canned Tomato Sauce", prefill.AmbiguousSuggestion);
    }

    [Fact]
    public void An_ambiguous_suggestion_that_IS_the_rows_own_name_carries_no_name()
    {
        // AmbiguousName states it live off the row's name, so carrying it too would put two sentences on one row.
        var catalog = Cat(P(1, "Ground Beef"), P(2, "Ground Beef"));
        var prefill = CensusPlan.Prefill(Read("Ground Beef", suggested: "Ground Beef"), catalog);
        Assert.Equal(0, prefill.ProductId);
        Assert.Null(prefill.AmbiguousSuggestion);
    }

    [Fact]
    public void Prefill_treats_a_punctuation_variant_of_the_read_name_as_an_identity_not_a_guess()
    {
        var catalog = Cat(P(1, "Home-Canned Tomato Sauce"));
        var prefill = CensusPlan.Prefill(Read("Home Canned Tomato Sauce"), catalog);
        Assert.Equal(1, prefill.ProductId);
        Assert.False(prefill.BySimilarity); // rule 1 — not flagged as a similarity guess
    }

    [Fact]
    public void Prefill_flags_a_similarity_match_as_a_guess()
    {
        var catalog = Cat(P(1, "Whole Milk"));
        var prefill = CensusPlan.Prefill(Read("Milk"), catalog); // "milk" ⊂ "whole milk"
        Assert.Equal(1, prefill.ProductId);
        Assert.True(prefill.BySimilarity);
    }

    [Fact]
    public void Prefill_leaves_a_read_name_two_products_share_unmatched()
    {
        var catalog = Cat(P(1, "Ground Beef"), P(2, "Ground Beef"));
        var prefill = CensusPlan.Prefill(Read("Ground Beef"), catalog);
        Assert.Equal(new CensusPlan.CensusPrefill(0, false, null), prefill);
    }

    [Fact]
    public void Prefill_pre_selects_nothing_for_a_wholly_novel_item()
    {
        var catalog = Cat(P(1, "Whole Milk"));
        Assert.Equal(new CensusPlan.CensusPrefill(0, false, null), CensusPlan.Prefill(Read("Tilapia Fillets"), catalog));
    }

    // ---- Plan: landing on a chosen product --------------------------------------------------------

    [Fact]
    public void A_chosen_product_with_a_real_count_lands_cleanly_and_ticks()
    {
        var catalog = Cat(P(1, "Whole Milk"));
        var plan = Plan1(Row("Whole Milk", 3m, chosen: 1), catalog);
        Assert.Equal(Action.LandOnProduct, plan.Action);
        Assert.Equal(1, plan.LandsOn);
        Assert.Equal(Reason.LandsOnProduct, plan.Reason);
        Assert.False(plan.NeedsAHumanLook);
        Assert.True(ArrivalTicks(plan, 0.9m));
        Assert.False(ArrivalTicks(plan, 0.5m)); // the other half of the tick rule: below the floor, no tick
    }

    [Fact]
    public void A_still_selected_similarity_match_lands_but_needs_a_look()
    {
        var catalog = Cat(P(1, "Whole Milk"));
        var plan = Plan1(Row("Milk", 2m, chosen: 1, similarity: true), catalog);
        Assert.Equal(Action.LandOnProduct, plan.Action);
        Assert.Equal(Reason.MatchedBySimilarity, plan.Reason);
        Assert.True(plan.NeedsAHumanLook);
        Assert.False(ArrivalTicks(plan, 0.99m)); // a perfect read of the ITEM still doesn't authorize the target
    }

    [Fact]
    public void A_chosen_product_that_has_vanished_is_refused()
    {
        var plan = Plan1(Row("Whole Milk", 3m, chosen: 4242), Cat(P(1, "Whole Milk")));
        Assert.Equal(Action.Refuse, plan.Action);
        Assert.Equal(Reason.ProductGone, plan.Reason);
        Assert.True(plan.NeedsAHumanLook);
    }

    [Fact]
    public void A_chosen_product_with_an_empty_box_is_MissingCount_not_a_zero()
    {
        var plan = Plan1(Row("Whole Milk", null, chosen: 1), Cat(P(1, "Whole Milk")));
        Assert.Equal(Action.Refuse, plan.Action);
        Assert.Equal(Reason.MissingCount, plan.Reason);
    }

    [Fact]
    public void A_chosen_product_with_a_negative_count_is_refused()
    {
        var plan = Plan1(Row("Whole Milk", -3m, chosen: 1), Cat(P(1, "Whole Milk")));
        Assert.Equal(Reason.NegativeCount, plan.Reason);
        Assert.Equal(Action.Refuse, plan.Action);
    }

    [Fact]
    public void A_chosen_product_counted_at_zero_lands_on_it_it_is_not_ZeroOnNewProduct()
    {
        // A zero on an EXISTING product is a real asserted outage, not a phantom-creation refusal.
        var plan = Plan1(Row("Whole Milk", 0m, chosen: 1), Cat(P(1, "Whole Milk")));
        Assert.Equal(Action.LandOnProduct, plan.Action);
        Assert.Equal(1, plan.LandsOn);
    }

    // ---- Plan: create-new dropdown ----------------------------------------------------------------

    [Fact]
    public void A_novel_name_creates_a_product()
    {
        var plan = Plan1(Row("Tilapia Fillets", 3m), Cat(P(1, "Whole Milk")));
        Assert.Equal(Action.CreateProduct, plan.Action);
        Assert.Null(plan.LandsOn);
        Assert.Equal(Reason.CreatesProduct, plan.Reason);
        Assert.False(plan.NeedsAHumanLook);
        Assert.True(ArrivalTicks(plan, 0.9m));
    }

    [Fact]
    public void A_typed_name_that_resembles_an_existing_product_is_named_but_still_tickable()
    {
        // The row CREATES a separate item (fuzzy resemblance is never resolved) and shows a "looks like X"
        // heads-up — but does NOT withhold the tick: fuzzy matching false-positives, the human typed the name,
        // and bulk-ticking it to create a separate item is a valid flow (the app's standing NearMatch behaviour).
        var catalog = Cat(P(1, "93% Lean Ground Beef"));
        var plan = Plan1(Row("Ground Beef", 2m), catalog);
        Assert.Equal(Action.CreateProduct, plan.Action);
        Assert.Equal(Reason.ResemblesExisting, plan.Reason);
        Assert.False(plan.NeedsAHumanLook); // a soft warning, not a guess about which product gets overwritten
    }

    [Fact]
    public void A_typed_name_that_IS_an_existing_product_lands_on_it()
    {
        // The unmatched-name-resolves-to-existing rule (retry safety), said so the screen matches the write.
        var catalog = Cat(P(7, "Ground Beef"));
        var plan = Plan1(Row("ground beef", 4m), catalog); // different casing, dropdown at create-new
        Assert.Equal(Action.LandOnProduct, plan.Action);
        Assert.Equal(7, plan.LandsOn);
        Assert.Equal(Reason.WillLandOnExisting, plan.Reason);
        Assert.False(plan.NeedsAHumanLook);
    }

    [Fact]
    public void A_typed_name_two_products_share_is_refused_as_ambiguous()
    {
        var catalog = Cat(P(1, "Ground Beef"), P(2, "Ground Beef"));
        var plan = Plan1(Row("Ground Beef", 4m), catalog);
        Assert.Equal(Action.Refuse, plan.Action);
        Assert.Equal(Reason.AmbiguousName, plan.Reason);
        Assert.True(plan.NeedsAHumanLook);
    }

    [Fact]
    public void An_ambiguous_suggestion_on_a_novel_name_needs_a_look()
    {
        var catalog = Cat(P(1, "Home-Canned Tomato Sauce"), P(2, "Home Canned Tomato Sauce"));
        var plan = Plan1(Row("Canned Tomato Sauce", 3m, ambiguousSuggestion: "Home Canned Tomato Sauce"), catalog);
        Assert.Equal(Action.CreateProduct, plan.Action);
        Assert.Equal(Reason.AmbiguousSuggestion, plan.Reason);
        Assert.True(plan.NeedsAHumanLook);
    }

    [Fact]
    public void Picking_a_product_clears_the_ambiguous_suggestion()
    {
        // The complement: once the human picks, the row lands cleanly and ticks — the guard was about a pick
        // nobody had made.
        var catalog = Cat(P(1, "Home-Canned Tomato Sauce"), P(2, "Home Canned Tomato Sauce"));
        var plan = Plan1(Row("Canned Tomato Sauce", 3m, chosen: 1, ambiguousSuggestion: "Home Canned Tomato Sauce"), catalog);
        Assert.Equal(Action.LandOnProduct, plan.Action);
        Assert.False(plan.NeedsAHumanLook);
    }

    [Fact]
    public void An_explicit_create_new_whose_name_is_taken_is_refused_as_NameTaken()
    {
        var catalog = Cat(P(1, "Ground Beef"));
        var plan = Plan1(Row("Ground Beef", 4m, createNew: true), catalog);
        Assert.Equal(Action.Refuse, plan.Action);
        Assert.Equal(Reason.NameTaken, plan.Reason);
    }

    [Fact]
    public void An_explicit_create_new_whose_name_is_a_punctuation_variant_is_still_NameTaken()
    {
        // Identity, not raw equality — the residual a raw-compare version minted a twin through.
        var catalog = Cat(P(1, "Half-and-Half"));
        var plan = Plan1(Row("Half and Half", 4m, createNew: true), catalog);
        Assert.Equal(Reason.NameTaken, plan.Reason);
    }

    [Fact]
    public void An_explicit_create_new_with_a_genuinely_novel_name_creates()
    {
        var plan = Plan1(Row("Quarter Cow Ground Beef", 8m, createNew: true), Cat(P(1, "Whole Milk")));
        Assert.Equal(Action.CreateProduct, plan.Action);
        Assert.Equal(Reason.CreatesProduct, plan.Reason);
    }

    [Fact]
    public void A_blank_name_with_no_product_is_refused()
    {
        var plan = Plan1(Row("   ", 3m), Cat(P(1, "Whole Milk")));
        Assert.Equal(Reason.NoName, plan.Reason);
        Assert.Equal(Action.Refuse, plan.Action);
    }

    [Fact]
    public void A_name_with_no_identity_content_is_refused_as_no_name()
    {
        // "!!" folds to an EMPTY identity key, which the identity system cannot see: ExactMatches refuses
        // empty keys and CatalogIndex never indexes one, so the name could only ever create a sight-unseen
        // twin — and every other junk name in the SAME census would share its empty key and merge with it
        // ("!!" 3 and "--" 2 becoming one product carrying five). To product identity it IS no name.
        var plans = CensusPlan.Plan([Row("!!", 3m), Row("--", 2m)], Cat());
        Assert.All(plans, p => Assert.Equal(Reason.NoName, p.Reason));
        Assert.All(plans, p => Assert.Equal(Action.Refuse, p.Action));
    }

    [Fact]
    public void A_junk_name_beside_a_dropdown_pick_still_lands()
    {
        // The complement: a dropdown pick names the product by ID, and the name box is free text — the
        // identity guard must sit on the CREATE path only, never refuse a row the id already settles.
        var plan = Plan1(Row("!!", 2m, chosen: 1), Cat(P(1, "Whole Milk")));
        Assert.Equal(Action.LandOnProduct, plan.Action);
        Assert.Equal(1, plan.LandsOn);
    }

    [Fact]
    public void A_create_row_with_an_empty_box_is_MissingCount()
    {
        var plan = Plan1(Row("Tilapia Fillets", null), Cat(P(1, "Whole Milk")));
        Assert.Equal(Reason.MissingCount, plan.Reason);
    }

    [Fact]
    public void A_create_row_with_a_negative_count_is_refused()
    {
        var plan = Plan1(Row("Tilapia Fillets", -1m), Cat(P(1, "Whole Milk")));
        Assert.Equal(Reason.NegativeCount, plan.Reason);
    }

    // ---- Plan: the evidence, independent of the name -----------------------------------------------

    [Fact]
    public void An_unidentified_row_needs_a_look_even_when_it_would_land_cleanly()
    {
        // "Name it" and the withheld tick ride on the evidence, so a row named into an ordinary match still
        // waits for a human — the tick authorizes a write, and an unidentified package is one the household
        // hasn't confirmed.
        var catalog = Cat(P(7, "Ground Beef"));
        var plan = Plan1(Row("ground beef", 3m, evidence: CensusEvidence.Unidentified), catalog);
        Assert.Equal(Action.LandOnProduct, plan.Action); // it DOES resolve...
        Assert.Equal(7, plan.LandsOn);
        Assert.True(plan.NeedsAHumanLook);               // ...but the evidence still withholds the tick
        Assert.False(ArrivalTicks(plan, 0.99m));
    }

    [Fact]
    public void An_unidentified_row_creating_a_product_also_needs_a_look()
    {
        var plan = Plan1(Row("foil-wrapped parcel", 4m, evidence: CensusEvidence.Unidentified), Cat(P(1, "Whole Milk")));
        Assert.Equal(Action.CreateProduct, plan.Action);
        Assert.True(plan.NeedsAHumanLook);
    }

    [Fact]
    public void A_would_land_on_existing_row_carrying_an_ambiguous_suggestion_withholds_the_tick()
    {
        // ⚠️ Finding A. The row's own name identity-matches ONE product (P1 "Tomato Sauce"), so it lands
        // there — but the reader SUGGESTED a twin ("Home Canned Tomato Sauce", which P2/P3 share). The two
        // read-time facts conflict, so the tick is withheld: auto-ticking would silently overwrite P1's
        // count while the twin the reader saw gets nothing. The OLD code returned look:false here and the
        // row auto-ticked at 0.95, because WillLandOnExisting returned before CreateReason ever saw the
        // suggestion. The complement (no ambiguous suggestion → auto-ticks) is
        // A_typed_name_that_IS_an_existing_product_lands_on_it above.
        var catalog = Cat(P(1, "Tomato Sauce"), P(2, "Home-Canned Tomato Sauce"), P(3, "Home Canned Tomato Sauce"));
        var plan = Plan1(Row("Tomato Sauce", 3m, ambiguousSuggestion: "Home Canned Tomato Sauce"), catalog);
        Assert.Equal(Action.LandOnProduct, plan.Action); // still resolves to P1 by name...
        Assert.Equal(1, plan.LandsOn);
        Assert.Equal(Reason.WillLandOnExisting, plan.Reason);
        Assert.True(plan.NeedsAHumanLook);               // ...but the conflicting read withholds the tick
        Assert.False(ArrivalTicks(plan, 0.95m));
    }

    // ---- Plan: whole-census (deferred zeros, sibling creates, twins) -------------------------------

    [Fact]
    public void Two_novel_rows_of_one_item_both_create_the_same_product()
    {
        var plans = CensusPlan.Plan([Row("Frozen Peas", 2m), Row("Frozen Peas", 3m)], Cat());
        Assert.All(plans, p => Assert.Equal(Action.CreateProduct, p.Action));
        Assert.All(plans, p => Assert.Equal(Reason.CreatesProduct, p.Reason));
    }

    [Theory]
    [InlineData(true)]  // zero first
    [InlineData(false)] // zero second
    public void A_zero_beside_a_real_count_of_the_same_new_item_joins_it_either_way(bool zeroFirst)
    {
        // ⚠️ Order-independence is the whole point of planning the census in one pass: a zero that a sibling
        // creates the product for CONTRIBUTES its zero, it does not refuse — and which ordering the reader
        // emitted must not decide that.
        CensusPlan.CensusRowState[] rows = zeroFirst
            ? [Row("Sardines", 0m), Row("Sardines", 2m)]
            : [Row("Sardines", 2m), Row("Sardines", 0m)];

        var plans = CensusPlan.Plan(rows, Cat());

        Assert.All(plans, p => Assert.Equal(Action.CreateProduct, p.Action)); // neither is refused
        Assert.DoesNotContain(plans, p => p.Reason == Reason.ZeroOnNewProduct);
    }

    [Fact]
    public void A_zero_on_a_novel_name_with_no_sibling_is_refused_as_ZeroOnNewProduct()
    {
        var plan = Plan1(Row("Frozen Peas", 0m), Cat());
        Assert.Equal(Action.Refuse, plan.Action);
        Assert.Equal(Reason.ZeroOnNewProduct, plan.Reason);
    }

    [Fact]
    public void Two_zeros_of_one_novel_item_with_no_positive_sibling_are_both_refused()
    {
        // Neither can bring the product into existence, so there is nothing for either to attach to.
        var plans = CensusPlan.Plan([Row("Frozen Peas", 0m), Row("Frozen Peas", 0m)], Cat());
        Assert.All(plans, p => Assert.Equal(Reason.ZeroOnNewProduct, p.Reason));
    }

    [Fact]
    public void A_zero_beside_a_PUNCTUATION_variant_of_the_same_new_item_joins_it()
    {
        // ⚠️ The reader emits a row per variety, transcribing a label with and without a hyphen — so the
        // deferred-zero settle-up keys on the matcher's IDENTITY, not the raw name, or this pair rebuilds the
        // row-order dependence the deferral exists to remove.
        var plans = CensusPlan.Plan([Row("Home Canned Sauce", 0m), Row("Home-Canned Sauce", 2m)], Cat());
        Assert.All(plans, p => Assert.Equal(Action.CreateProduct, p.Action));
    }

    [Fact]
    public void A_zero_on_an_existing_product_is_never_ZeroOnNewProduct_even_beside_a_novel_zero()
    {
        // The refusal is scoped to CREATION. A zero on a product the household owns is a real outage; only a
        // zero that would MINT a product is refused.
        var catalog = Cat(P(1, "Black Beans"));
        var plans = CensusPlan.Plan([Row("Black Beans", 0m, chosen: 1), Row("Frozen Peas", 0m)], catalog);
        Assert.Equal(Action.LandOnProduct, plans[0].Action); // the existing product's zero lands (asserts out)
        Assert.Equal(Reason.ZeroOnNewProduct, plans[1].Reason); // only the would-be-new one is refused
    }

    [Fact]
    public void A_refused_row_does_not_change_its_neighbours_plans()
    {
        var plans = CensusPlan.Plan([Row("Frozen Peas", 3m), Row("   ", 2m), Row("Black Beans", -1m)], Cat());
        Assert.Equal(Action.CreateProduct, plans[0].Action);
        Assert.Equal(Reason.NoName, plans[1].Reason);
        Assert.Equal(Reason.NegativeCount, plans[2].Reason);
    }

    [Fact]
    public void An_empty_census_plans_nothing()
    {
        Assert.Empty(CensusPlan.Plan([], Cat(P(1, "Whole Milk"))));
    }
}
