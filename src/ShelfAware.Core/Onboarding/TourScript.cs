namespace ShelfAware.Core.Onboarding;

/// <summary>
/// One stop on the guided walkthrough: the page it visits, what to say about it, and (optionally)
/// what to ring on that page.
///
/// <para><b>The copy must be true of ANY household's data, not just the demo catalog.</b> The tour is
/// offered to a real new user as well as to someone who just loaded the sample pantry, and a step that
/// asserts a specific row ("see the overdue roast at the top") is a screen stating something the engine
/// didn't do the moment it runs against real receipts. Describe the FEATURE and the controls; let the
/// page itself state the facts.</para>
/// </summary>
/// <param name="Route">Relative URL the walkthrough navigates to for this step.</param>
/// <param name="Title">Short heading — what this screen is for.</param>
/// <param name="Body">One or two sentences. Longer than that and it stops being read.</param>
/// <param name="Anchor">CSS selector for the element to ring, or null for no highlight. A selector that
/// matches nothing is not an error: the step still reads, just without the ring. That is deliberate —
/// a walkthrough must degrade to plain instructions rather than break on a page whose layout moved.</param>
public sealed record TourStep(string Route, string Title, string Body, string? Anchor);

/// <summary>
/// The walkthrough itself, and the (pure) rules for moving through it.
///
/// Lives in Core rather than in the component for the reason <c>MealStock</c> and <c>SpendForecast</c>
/// do: logic private to a page is logic no test can reach. Here the step list, the clamping and the
/// end-of-tour boundary are all assertable without a renderer.
/// </summary>
public static class TourScript
{
    /// <summary>
    /// The default anchor: a page's title block. Every page in the app opens with either a
    /// <c>.page-head</c> wrapper or a bare <c>&lt;h1&gt;</c>, and the wrapper — where it exists — starts
    /// before the heading it contains, so document order resolves this to the outermost of the two.
    /// One selector that means "this page's title" everywhere.
    /// </summary>
    private const string PageTitle = ".page-head, h1";

    /// <summary>
    /// The tour, in order. Covers each surface a new household needs to find on its own; the
    /// deeper mechanics (counting, expiration labels, merges, report building) are deliberately left
    /// to the pages that explain them — a walkthrough long enough to cover everything is one nobody
    /// finishes.
    /// </summary>
    public static IReadOnlyList<TourStep> Steps { get; } =
    [
        new("/", "What you're about to run out of",
            "This is the app's answer to “what do we need?” — what's running low, most urgent first. "
            + "It works the rhythm out from your receipts, so nothing here is a reminder you had to set yourself.",
            PageTitle),

        new("/", "Just tell it",
            "Type it the way you'd say it — “we're out of dog food, almost out of coffee” — and the list "
            + "updates on the spot. The \U0001F399 Assistant does the same hands-free, and keeps listening as you move around.",
            ".quick-update"),

        new("/products", "Everything you track",
            "Every item you buy, with its usual brand and size, and an Out button whenever you need it. "
            + "Click any name for how often you really buy it, what you've paid over time, and how many you have on hand.",
            PageTitle),

        new("/list", "Shop by aisle",
            "The buy list in the order you walk the store, with a running estimated total, ready to print or copy. "
            + "Add anything by hand under Extras — or send a recipe's missing ingredients straight to it.",
            PageTitle),

        new("/recipes", "Cook what you have",
            "Recipes marked by what's actually on your shelves. Ask for ideas, adapt a saved one to what's in tonight, "
            + "or start a hands-free cook-along that reads the steps out and takes “next” while your hands are full.",
            PageTitle),

        new("/receipt", "Start with a receipt",
            "Photograph or upload a grocery receipt and the lines are read and matched to what you already buy. "
            + "You review before anything is recorded — and can let confident imports through on their own once you trust it.",
            PageTitle),

        new("/receipts", "Every receipt you've imported",
            "What each shop actually cost, line by line. Import the same receipt twice and it's caught rather than "
            + "silently double-counted, and any receipt can be removed — undoing everything recording it did.",
            PageTitle),

        new("/trends", "Prices and what's ahead",
            "What each item has cost over time, and a forecast of the weeks ahead built from the same rhythms driving "
            + "your list — so a big shop is something you see coming.",
            PageTitle),

        new("/reports", "Reports you can print",
            "Start from a ready-made report or build your own, on screen or on paper. "
            + "The chart and the table beneath it always say the same thing, so a printed copy loses nothing.",
            PageTitle),

        new("/accuracy", "How well it actually works",
            "The honest scorecard: how accurately receipts are read, and how well the predictions held up when "
            + "replayed against your own history. Both are measured, not claimed.",
            PageTitle),

        new("/settings", "Your key, your data",
            "Shelf Aware runs on your own API key — it stays in your browser and is never stored on the server. "
            + "Add one here to switch the assistant on, and export or delete everything whenever you want.",
            "[data-tour=ai-keys]"),
    ];

    /// <summary>Number of stops in the walkthrough.</summary>
    public static int Count => Steps.Count;

    /// <summary>
    /// The step at <paramref name="index"/>, clamped into range. A stored position is read back from the
    /// visitor's browser, where it can be stale (a shorter tour shipped since) or edited by hand, so this
    /// never throws: an out-of-range position resolves to the nearest real step.
    /// </summary>
    public static TourStep At(int index) => Steps[ClampIndex(index)];

    /// <summary>Clamps a position into the range of real steps.</summary>
    public static int ClampIndex(int index) => Math.Clamp(index, 0, Steps.Count - 1);

    /// <summary>True when <paramref name="index"/> is the last step — the point at which "Next" becomes "Done".</summary>
    public static bool IsLast(int index) => ClampIndex(index) == Steps.Count - 1;

    /// <summary>True when <paramref name="index"/> is the first step, so "Back" has nowhere to go.</summary>
    public static bool IsFirst(int index) => ClampIndex(index) == 0;
}
