using Microsoft.AspNetCore.Components;

namespace ShelfAware.Web.Components.Layout;

/// <summary>THE list of the app's top-level pages, grouped into the menu's sections. The rail renders
/// one link per section, a section with more than one page renders its pages as a tab strip
/// (<see cref="SectionTabs"/>), and the bug-report form's "where" dropdown is built from the same
/// pages — so a page added here appears in all three and they can never drift. That's the
/// one-accessible-definition rule, applied to navigation. Order is the rail's reading order, and a
/// section's first page is where its menu link lands.</summary>
public static class SiteNav
{
    public sealed record Page(string Label, string Href);

    public sealed record Section(string Label, IReadOnlyList<Page> Pages)
    {
        /// <summary>Where the section's menu link goes: its first page.</summary>
        public Page Home => Pages[0];

        /// <summary>Whether the visitor is on any of this section's pages.</summary>
        public bool Contains(string path) => Pages.Any(p => IsAt(p, path));
    }

    public static readonly IReadOnlyList<Section> Sections =
    [
        new("Dashboard", [new("Dashboard", "/")]),
        new("Grocery List", [new("Grocery List", "/list")]),
        new("Recipes", [new("Recipes", "/recipes"), new("Cookbook", "/cookbook")]),
        new("Meal Plan", [new("Meal Plan", "/meal-plan")]),
        new("Journal", [new("Journal", "/journal")]),
        new("Receipts", [new("Receipts", "/receipts"), new("Upload", "/receipt"), new("Count Stock", "/pantry-photo")]),
        new("Products", [new("Products", "/products"), new("History", "/history")]),
        new("Insights", [new("Trends", "/trends"), new("Reports", "/reports"), new("Accuracy", "/accuracy")]),
        new("Settings", [new("Settings", "/settings")]),
    ];

    /// <summary>Every page, in reading order — what the bug form offers and allowlists against.</summary>
    public static readonly IReadOnlyList<Page> Pages = [.. Sections.SelectMany(s => s.Pages)];

    /// <summary>The section the visitor is in, or null on a page outside the menu (a product's own
    /// page, the bug form, the admin page).</summary>
    public static Section? SectionAt(string path) => Sections.FirstOrDefault(s => s.Contains(path));

    /// <summary>Whether <paramref name="path"/> is <paramref name="page"/> or somewhere beneath it.
    /// The match is segment-aware, so /receipt is not "at" /receipts, while /cookbook/import is at
    /// /cookbook. The dashboard is "/", which everything is beneath, so it only matches exactly.</summary>
    public static bool IsAt(Page page, string path) =>
        page.Href == "/"
            ? path == "/"
            : path.Equals(page.Href, StringComparison.OrdinalIgnoreCase)
              || path.StartsWith(page.Href + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The app path of an absolute URI: "/" plus the base-relative part, with any query
    /// string or fragment dropped, since neither changes which page the visitor is on.</summary>
    public static string PathOf(NavigationManager nav, string absoluteUri)
    {
        var relative = nav.ToBaseRelativePath(absoluteUri);
        var end = relative.IndexOfAny(['?', '#']);
        return "/" + (end < 0 ? relative : relative[..end]);
    }
}
