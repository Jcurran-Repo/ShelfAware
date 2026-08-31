using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;

namespace ShelfAware.Core.MealPlanning;

/// <summary>
/// Generates the meals for a batch of slots from the household's setup + pantry, in ONE AI call. The
/// service batches a long horizon into several calls (a week at a time), passing the already-planned names
/// so the model keeps the plan varied.
/// <para>Generation is <b>adapt-known-first</b>: reuse/adapt the household's saved recipes and well-known
/// dishes, prefer on-hand + commonly-bought ingredients, use what's expiring first, and write
/// method-correct steps for whatever it uses (a tough cut is braised, not seared) — never invent a novel
/// dish or reach for exotic ingredients unless <see cref="MealPlanSettings.Invent"/>. It returns one recipe
/// per requested slot, IN ORDER, so the caller zips them back to their (day, slot).</para>
/// </summary>
public interface IMealPlanGenerator
{
    Task<IReadOnlyList<RecipeSuggestion>> GenerateAsync(MealPlanBatch batch, CancellationToken cancellationToken = default);
}

/// <summary>One eating occasion to fill: a day offset from the plan's start (0-based) and its slot.</summary>
public record PlannedSlot(int Day, MealSlot Slot);

/// <summary>Everything one generation call needs: the slots to fill (in order), the household's setup, and
/// the pantry context for grounding + variety. Names only — the model reasons over names, not entities.</summary>
public record MealPlanBatch(
    IReadOnlyList<PlannedSlot> Slots,
    MealPlanSettings Settings,
    IReadOnlyList<string> OnHand,             // on-hand product names — prefer using these
    IReadOnlyList<string> CommonlyBought,      // purchase-history names — the "familiar" ingredient palette
    IReadOnlyList<string> ExpiringSoon,        // on-hand items to use first (may be empty)
    IReadOnlyList<string> ExcludedFoods,       // hard-exclude entirely
    IReadOnlyList<string> InspirationRecipes,  // the household's saved recipe names (adapt-known)
    IReadOnlyList<string> AvoidNames);         // meals already planned this run — keep it varied
