namespace ShelfAware.Core.Prediction;

/// <summary>
/// THE one definition of how a <see cref="PredictionStatus"/> and a due-date distance are shown — the chip
/// class, the status label, and the two urgency phrasings the app uses. Every surface that renders a
/// prediction asks this rather than keeping its own copy.
///
/// <para>It exists because four pages kept byte-identical private copies of <c>ChipClass</c> and
/// <c>StatusLabel</c>, and three kept near-identical copies of the day phrasing — the shape the top of
/// CLAUDE.md names as this repo's most expensive failure, and the one that shipped "1 days over" twice and
/// a Stocked chip beside overdue text twice. A chip class is a fact shown in more than one place, so it
/// gets one accessible definition. The sibling precedent is
/// <see cref="Recipes.MakeabilityFormat"/>, which this follows deliberately.</para>
///
/// <para>In Core because it is pure, and because Core is gated at 100% mutation coverage — the right place
/// for a definition four surfaces depend on and none of them can see the inside of.</para>
/// </summary>
public static class PredictionDisplay
{
    /// <summary>The status-chip CSS modifier: <c>overdue</c> / <c>duesoon</c> / <c>stocked</c> /
    /// <c>unknown</c>. Callers compose it (<c>chip chip-@ChipClass(s)</c>) the way the pages already did.</summary>
    public static string ChipClass(PredictionStatus status) => status switch
    {
        PredictionStatus.Overdue => "overdue",
        PredictionStatus.DueSoon => "duesoon",
        PredictionStatus.Stocked => "stocked",
        _ => "unknown",
    };

    /// <summary>The human label on the chip. <see cref="PredictionStatus.Unknown"/> reads "Still learning"
    /// rather than "Unknown" — the app's standing phrasing for thin data, which is a promise about what
    /// happens next rather than an admission.</summary>
    public static string Label(PredictionStatus status) => status switch
    {
        PredictionStatus.Overdue => "Overdue",
        PredictionStatus.DueSoon => "Due soon",
        PredictionStatus.Stocked => "Stocked",
        _ => "Still learning",
    };

    /// <summary>The standalone sentence form, for a card or a headline: "2 days overdue", "Due today",
    /// "Due in 3 days". <paramref name="days"/> is the due date minus today, so negative is late.</summary>
    public static string Urgency(int days) => days switch
    {
        _ when days < 0 => Overdue(days),
        0 => "Due today",
        _ => $"Due in {days} day{Plural(days)}",
    };

    /// <summary>The inline phrase form, for a cell that already sits under a "Next buy" heading:
    /// "2 days overdue", "today", "in 3 days". Same reading of <paramref name="days"/> as
    /// <see cref="Urgency"/> — the two differ only in whether they carry their own subject.</summary>
    public static string Relative(int days) => days switch
    {
        _ when days < 0 => Overdue(days),
        0 => "today",
        _ => $"in {days} day{Plural(days)}",
    };

    /// <summary>The late phrasing, shared by both forms above so it cannot drift between them. It is the
    /// one that has actually gone wrong: "1 days over" shipped twice, from two surfaces that each spelled
    /// this out for themselves.</summary>
    private static string Overdue(int days) => $"{-days} day{Plural(-days)} overdue";

    /// <summary>The plural suffix: "" for exactly one, "s" otherwise. English only, and deliberately naive
    /// — every caller here counts days.</summary>
    public static string Plural(int n) => n == 1 ? "" : "s";
}
