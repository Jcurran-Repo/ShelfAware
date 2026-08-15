namespace ShelfAware.Core.Recipes;

/// <summary>How a recipe ingredient reads on one line — the amount then the name ("2 lbs chicken
/// breast"), or just the name when the recipe gave no amount. One definition shared by the Recipes
/// page, the cookbook, and the printed products list, so the same ingredient can never be phrased three
/// different ways across the surfaces that show it.</summary>
public static class RecipeFormat
{
    /// <summary>"2 lbs chicken breast" when a quantity is present, else just "chicken breast".</summary>
    public static string Line(string? quantity, string name) =>
        string.IsNullOrWhiteSpace(quantity) ? name : $"{quantity} {name}";
}
