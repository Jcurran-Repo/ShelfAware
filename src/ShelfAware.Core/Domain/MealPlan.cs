namespace ShelfAware.Core.Domain;

/// <summary>A generated meal plan — a horizon of dated meal slots, each pointing at a (plan-generated)
/// recipe. One active plan per household: regenerating replaces it. The setup that drove generation lives
/// in the household's AppSettings (its editable preferences), not on the plan.</summary>
public class MealPlan : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }

    /// <summary>When this plan was generated.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The plan's first day.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>How many days the plan covers.</summary>
    public int Days { get; set; }

    /// <summary>The dated meal slots, deleted with the plan (EF cascade).</summary>
    public List<PlannedMeal> Meals { get; set; } = [];
}
