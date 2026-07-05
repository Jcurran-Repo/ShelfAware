namespace ShelfAware.Core.Domain;

public class RecipeIngredient
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    public required string Name { get; set; }
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
}
