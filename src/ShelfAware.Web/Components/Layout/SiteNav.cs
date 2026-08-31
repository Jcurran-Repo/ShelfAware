namespace ShelfAware.Web.Components.Layout;

/// <summary>THE list of the app's top-level pages. The header nav renders it and the bug-report
/// form's "where" dropdown is built from it, so a page added here appears in both and the two can
/// never drift — the one-accessible-definition rule, applied to navigation. Order is the header's
/// reading order.</summary>
public static class SiteNav
{
    public sealed record Page(string Label, string Href);

    public static readonly IReadOnlyList<Page> Pages =
    [
        new("Dashboard", "/"),
        new("Grocery List", "/list"),
        new("Recipes", "/recipes"),
        new("Cookbook", "/cookbook"),
        new("Meal Plan", "/meal-plan"),
        new("Trends", "/trends"),
        new("Reports", "/reports"),
        new("Upload", "/receipt"),
        new("Receipts", "/receipts"),
        new("Count Stock", "/pantry-photo"),
        new("Products", "/products"),
        new("History", "/history"),
        new("Accuracy", "/accuracy"),
        new("Settings", "/settings"),
    ];
}
