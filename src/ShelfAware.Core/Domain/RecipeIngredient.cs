using ShelfAware.Core.Recipes;

namespace ShelfAware.Core.Domain;

public class RecipeIngredient
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public required string Name { get; set; }
    /// <summary>Free-form amount as the recipe calls for it ("2 lbs", "3 cloves", "1 (14 oz) can", "a pinch"),
    /// or null if unspecified. Display + read-aloud guidance only — it does NOT affect makeability (we don't
    /// track how much of a product is on hand), matching the app's deliberate "no unit arithmetic" stance.</summary>
    public string? Quantity { get; set; }
    /// <summary>True for real/main ingredients (protein, veg, starch) that decide makeability; false for
    /// seasonings, spices, oils, and pantry staples (suggestion-only, excluded from the makeable check).</summary>
    public bool IsMain { get; set; }
    /// <summary>The catalog product this ingredient mapped to at save time (the LLM's semantic match), or
    /// null if the user didn't have it. The makeability check re-tests this name against current on-hand
    /// products — plain code, no repeat LLM call.</summary>
    public string? MatchedProduct { get; set; }
    /// <summary>Cached JSON array of interchangeable forms for this ingredient (e.g. chicken breast →
    /// ["chicken thighs","chicken tenderloins",…]), generated once on demand for the swap bubble-cloud so
    /// re-opening it costs no AI call. Null until first requested.</summary>
    public string? AlternativesJson { get; set; }

    /// <summary>True when something on hand covers this ingredient — its exact matched product, an on-hand
    /// product of the same specific food, or a product that lists it as a substitute (IngredientMatcher).</summary>
    public bool IsSatisfiedBy(IReadOnlyCollection<PantryProduct> onHand) =>
        IngredientMatcher.IsSatisfied(Name, MatchedProduct, onHand);
}
