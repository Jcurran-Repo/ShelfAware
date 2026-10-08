namespace ShelfAware.Core.Domain;

/// <summary>One thing one person ate: a food, the meal it was part of, the day, and its calories.
///
/// <para>One row per FOOD, not per meal — "two eggs and toast for breakfast" is two rows in the Breakfast
/// slot. A meal's calories are then a sum the journal computes (<c>MealJournal</c>), never a number stored
/// beside the items it would have to agree with, and a single item can be corrected without re-typing the
/// meal around it.</para>
///
/// <para>Private to the person (<see cref="IMemberOwned"/>), unlike every other pantry table. Deliberately
/// NOT written to the activity log either: History is household-wide, so an undo entry would put one
/// member's meals on the other's screen. Deleting the row on the journal page is its undo.</para></summary>
public class JournalEntry : IMemberOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public string MemberId { get; set; } = "";

    /// <summary>The day it was eaten — server-local, the same convention as every other date in the app.</summary>
    public DateOnly EatenOn { get; set; }

    /// <summary>Which meal it was part of. The same four occasions the meal planner plans for.</summary>
    public MealSlot Slot { get; set; }

    /// <summary>What was eaten, as the person put it ("2 scrambled eggs").</summary>
    public required string Food { get; set; }

    /// <summary>Calories for the portion eaten, or null when nobody could say. A null is counted as
    /// "no calorie count" and disclosed beside every total, never silently treated as zero.</summary>
    public int? Calories { get; set; }

    /// <summary>True when <see cref="Calories"/> is an estimate — Reginald's, or a saved recipe's per-serving
    /// figure — rather than a number the person stated or typed. Every total that includes one says so.</summary>
    public bool CaloriesEstimated { get; set; }

    /// <summary>When it was logged (not when it was eaten) — orders a day's items as they were told.</summary>
    public DateTimeOffset LoggedAt { get; set; }
}
