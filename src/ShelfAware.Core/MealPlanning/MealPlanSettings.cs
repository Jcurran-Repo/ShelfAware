using ShelfAware.Core.Domain;

namespace ShelfAware.Core.MealPlanning;

/// <summary>How ambitious the generated meals should be — the "time &amp; effort" slider. Quick = fast,
/// simple, few ingredients (weeknight); Ambitious = involved techniques welcome. The realism lever.</summary>
public enum TimeEffort { Quick, Standard, Ambitious }

/// <summary>The household's meal-plan setup — everything optional, stored as JSON in AppSettings. The empty
/// defaults mean "real cooked meals, high variety, use my pantry + what's expiring; don't invent". A snack
/// is just a small meal in the <see cref="MealSlot.Snack"/> slot — no special-casing here.</summary>
public sealed class MealPlanSettings
{
    /// <summary>How many days the plan covers.</summary>
    public int Days { get; init; } = 7;

    /// <summary>Which eating occasions to plan each day. Default: dinner only.</summary>
    public IReadOnlyList<MealSlot> Slots { get; init; } = [MealSlot.Dinner];

    /// <summary>Rough calorie target per meal, or null for no target.</summary>
    public int? CaloriesPerMeal { get; init; }

    /// <summary>Rough daily protein target (grams), or null.</summary>
    public int? ProteinGramsPerDay { get; init; }

    /// <summary>Rough daily carb target (grams), or null.</summary>
    public int? CarbGramsPerDay { get; init; }

    /// <summary>Food groups to cover / balance across the plan (free-form: "vegetables", "whole grains",
    /// "lean protein"…). Empty = no explicit balance target.</summary>
    public IReadOnlyList<string> FoodGroups { get; init; } = [];

    /// <summary>How ambitious the meals should be.</summary>
    public TimeEffort Effort { get; init; } = TimeEffort.Standard;

    /// <summary>Appliances the household has BEYOND oven + stovetop (slow cooker, air fryer, grill…), which
    /// the generator may use. Empty = oven + stovetop only.</summary>
    public IReadOnlyList<string> Appliances { get; init; } = [];

    /// <summary>When false (default), stick to known dishes and commonly-bought ingredients. When true, the
    /// generator may create novel dishes and reach for less-common ingredients.</summary>
    public bool Invent { get; init; }
}
