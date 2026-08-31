using ShelfAware.Core.Domain;

namespace ShelfAware.Core.MealPlanning;

/// <summary>How ambitious the generated meals should be — the "time &amp; effort" slider. Quick = fast,
/// simple, few ingredients (weeknight); Ambitious = involved techniques welcome. The realism lever.</summary>
public enum TimeEffort { Quick, Standard, Ambitious }

/// <summary>
/// ONE meal in the daily line-up — a slot (breakfast / lunch / dinner / snack) with OPTIONAL per-meal
/// overrides of the two things that really vary meal to meal: calories and effort. A null override inherits
/// the plan default (<see cref="MealPlanSettings.DefaultCalories"/> / <see cref="MealPlanSettings.DefaultEffort"/>),
/// so the common case is just "pick a slot" and the snack row can still say "150 cal, quick" while dinner
/// stays 600. The same meal repeats every day of the plan; the list is 1–7 meals long (breakfast, lunch,
/// dinner, and a few snacks for the small-meals crowd).
/// </summary>
public sealed class MealEntry
{
    public MealSlot Slot { get; init; } = MealSlot.Dinner;

    /// <summary>Per-meal calorie target, or null to inherit <see cref="MealPlanSettings.DefaultCalories"/>.</summary>
    public int? Calories { get; init; }

    /// <summary>Per-meal effort, or null to inherit <see cref="MealPlanSettings.DefaultEffort"/>.</summary>
    public TimeEffort? Effort { get; init; }
}

/// <summary>The household's meal-plan setup — everything optional, stored as JSON in AppSettings. The empty
/// defaults mean "one real cooked dinner a day, high variety, use my pantry + what's expiring; don't invent".
/// The setup fields are DEFAULTS; <see cref="Meals"/> is the per-meal line-up that inherits them and can
/// override calories/effort per meal (so a snack is genuinely snack-sized, not dinner-sized).</summary>
public sealed class MealPlanSettings
{
    /// <summary>The most meals a single day can hold (breakfast, lunch, dinner + up to four snacks for the
    /// frequent-small-meals pattern). A guard on the line-up, not a target.</summary>
    public const int MaxMealsPerDay = 7;

    /// <summary>How many days the plan covers.</summary>
    public int Days { get; init; } = 7;

    /// <summary>The meals to plan each day (repeated across the plan). Default: one dinner (a bare
    /// <see cref="MealEntry"/> already defaults its slot to <see cref="MealSlot.Dinner"/>).</summary>
    public IReadOnlyList<MealEntry> Meals { get; init; } = [new MealEntry()];

    /// <summary>Default calorie target a meal inherits when it sets no override, or null for no target.</summary>
    public int? DefaultCalories { get; init; }

    /// <summary>Default effort a meal inherits when it sets no override.</summary>
    public TimeEffort DefaultEffort { get; init; } = TimeEffort.Standard;

    /// <summary>Rough daily protein target (grams), or null. Per DAY — a daily total, not per meal.</summary>
    public int? ProteinGramsPerDay { get; init; }

    /// <summary>Rough daily carb target (grams), or null. Per DAY — a daily total, not per meal.</summary>
    public int? CarbGramsPerDay { get; init; }

    /// <summary>Food groups to cover / balance across the plan (free-form: "vegetables", "whole grains",
    /// "lean protein"…). Empty = no explicit balance target. Plan-level (balance is a whole-plan property).</summary>
    public IReadOnlyList<string> FoodGroups { get; init; } = [];

    /// <summary>Appliances the household has BEYOND oven + stovetop (slow cooker, air fryer, grill…), which
    /// the generator may use. Empty = oven + stovetop only.</summary>
    public IReadOnlyList<string> Appliances { get; init; } = [];

    /// <summary>When false (default), stick to known dishes and commonly-bought ingredients. When true, the
    /// generator may create novel dishes and reach for less-common ingredients.</summary>
    public bool Invent { get; init; }

    /// <summary>When true, deliberately cook once and eat twice — plan a larger dinner and reuse it as a
    /// following lunch — to cut cooking effort and waste. Off by default.</summary>
    public bool PreferLeftovers { get; init; }

    /// <summary>The calorie target for a given meal — its own override, or the plan default.</summary>
    public int? CaloriesFor(MealEntry meal) => meal.Calories ?? DefaultCalories;

    /// <summary>The effort for a given meal — its own override, or the plan default.</summary>
    public TimeEffort EffortFor(MealEntry meal) => meal.Effort ?? DefaultEffort;
}
