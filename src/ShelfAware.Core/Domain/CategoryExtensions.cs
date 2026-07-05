namespace ShelfAware.Core.Domain;

public static class CategoryExtensions
{
    /// <summary>Food aisles only — everything but household / pet / personal-care. Recipes reason over
    /// edible products so a "chicken" ingredient can't be satisfied by "Chicken Jerky Dog Treats".</summary>
    public static bool IsEdible(this Category c) =>
        c is not (Category.Household or Category.PetCare or Category.PersonalCare);
}
