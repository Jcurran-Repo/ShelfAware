namespace ShelfAware.Core.Domain;

/// <summary>One "Ate it" tap: this recipe was cooked on this day. <see cref="Recipe.TimesEaten"/> keeps
/// the lifetime total (the "Pick for me" reliability signal predates this table and counts taps from
/// before it existed); this row carries the WHEN, which is what lets reports chart meals and calories
/// over time. History that predates the table has no dates and is deliberately not fabricated — the
/// counter and the event log are allowed to disagree by exactly that pre-table remainder.</summary>
public class MealEvent : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }
    /// <summary>The day it was eaten — server-local "today" at tap time, same convention as every
    /// other date in the app (see the timezone deploy note in CLAUDE.md).</summary>
    public DateOnly AteAt { get; set; }
}
