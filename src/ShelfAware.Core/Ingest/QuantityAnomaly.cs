using System.Text.RegularExpressions;
using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Ingest;

/// <summary>
/// Catches a receipt line whose <c>Quantity</c> is really a misread PACK COUNT — a 12-pack of toilet
/// paper extracted as quantity 12 instead of "1 × 12-pack, size 12 rolls". The pack count belongs in
/// <c>Size</c>; leaking it into Quantity makes <see cref="Prediction.ReplenishmentPredictor"/> stretch
/// the due date by the pack size (a 12× stock-up), so a normal weekly buy reads as "not due for ~200
/// days" and the reminder never comes. This is a real bug seen on the family box, 2026-08-26.
///
/// <para>⚠️ Deliberately PRECISE, not broad. It does NOT flag on quantity size alone, because a genuine
/// stock-up — buy twelve when you usually buy one — is a real thing the engine is built to honour (the
/// same reason the stock-up factor is uncapped, CLAUDE.md item 19). A raw "quantity is much larger than
/// usual" test cannot tell a stock-up from a misread; only the SIZE evidence can, so that is all this
/// looks at. The engine's own note says a count (§13) is what truly answers "do I have twelve or one" —
/// this is the cheaper guard for the specific case where the count came from the pack.</para>
///
/// <para>It never silently corrects: a flag routes the line to human review (and blocks silent
/// auto-confirm), where the person decides — machine inference is not ground truth (§13.4).</para>
/// </summary>
public static class QuantityAnomaly
{
    /// <summary>Below this a quantity is an ordinary multi-buy, not a pack count worth second-guessing.
    /// The pack sizes people actually stock — and misread — start around here (6 / 8 / 12 / 24).</summary>
    public const int MinSuspiciousQuantity = 4;

    /// <summary>Whether this line's quantity looks like a pack count misread into the quantity field.
    /// <paramref name="priorSizes"/> is the resolved product's earlier purchases' size strings — empty
    /// for a brand-new product, where only the size-matches-quantity tell can fire.</summary>
    public static QuantityFlag Check(
        decimal quantity, string? lineSize, IReadOnlyCollection<string?> priorSizes)
    {
        // Pack counts are whole numbers; a weight (2.31 lb) never is, and a small count is an ordinary
        // multi-buy. Both must hold before the size evidence even gets a look.
        if (quantity < MinSuspiciousQuantity || quantity != decimal.Truncate(quantity))
            return QuantityFlag.None;

        // The size's own COUNT equals the quantity ("12 ct" + qty 12). ⚠️ Gated on the size being a
        // COUNT, not a weight/volume: a MEASURE size whose number happens to equal the quantity is an
        // ordinary multi-buy — twelve 12-oz cans, six 6-oz yogurts — not a misread. (Found by review.)
        if (LeadingCount(lineSize) == quantity && IsCountSize(lineSize))
            return QuantityFlag.SizeMatchesQuantity;

        // A count-shaped quantity with NO size, on a product that usually carries a COUNT size — the pack
        // count leaked into quantity. ⚠️ "Usually a COUNT size", not merely "usually sized": a
        // measure-sized item (16 oz) bought several times with the size dropped is a legit multi-buy.
        if (string.IsNullOrWhiteSpace(lineSize) && UsuallyHasACountSize(priorSizes))
            return QuantityFlag.MissingUsualSize;

        return QuantityFlag.None;
    }

    // Weight/volume units — a size carrying one is a per-unit MEASURE, not a pack count. A closed set
    // for the grocery domain (length units like "in"/"ft" essentially never size a grocery item).
    private static readonly HashSet<string> MeasureUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "oz", "ounce", "ounces", "lb", "lbs", "pound", "pounds", "g", "gr", "gram", "grams", "kg", "mg",
        "ml", "cl", "dl", "l", "liter", "liters", "litre", "litres", "gal", "gallon", "gallons",
        "qt", "quart", "quarts", "pt", "pint", "pints", "floz", "fl", "cup", "cups", "tbsp", "tbs", "tsp", "cc",
    };

    // A size that denotes a COUNT / pack ("12 ct", "6 Mega Roll", "18 eggs", "24pk", a bare "12") rather
    // than a weight or volume — the only kind whose number matching the quantity points to a pack-count
    // misread. Reads the LETTER runs, so "12oz"/"5lb" with no space are caught as measures too, not just
    // "12 oz" (regex ignores digits and separators); any measure unit among them disqualifies it.
    private static bool IsCountSize(string? size)
    {
        if (LeadingCount(size) is null) return false; // must start with a number to be a count at all
        var units = Regex.Matches(size!, "[A-Za-z]+").Select(m => m.Value);
        return !units.Any(MeasureUnits.Contains);
    }

    /// <summary>THE user-facing wording for a flag — one definition, so /receipts and the Upload
    /// done-panel can't phrase the same concern differently. Deliberately a question, not an accusation:
    /// it's a soft "we might have oopsied", and the human decides.</summary>
    public static string Describe(QuantityFlag flag, decimal quantity, string? size)
    {
        var q = quantity.ToString("0.##");
        return flag switch
        {
            QuantityFlag.SizeMatchesQuantity =>
                $"Recorded as a quantity of {q}, but the size “{size}” is that same count — did you buy one pack, not {q}?",
            QuantityFlag.MissingUsualSize =>
                $"Recorded as a quantity of {q} with no pack size, though this item usually has one — is this one {q}-pack?",
            _ => "",
        };
    }

    /// <summary>The leading run of digits of a size string as a whole number ("12 ct" → 12,
    /// "6 Mega Roll" → 6, "1 gal" → 1), or null when it doesn't start with digits ("lb", "").</summary>
    public static decimal? LeadingCount(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return null;
        var digits = new string(size.TrimStart().TakeWhile(char.IsDigit).ToArray());
        return decimal.TryParse(digits, out var n) ? n : null;
    }

    // "Usually a count size" = more than half of the existing prior purchases carried a COUNT size (a
    // pack, not a weight/volume). A measure-sized item bought several times with the size dropped once is
    // an ordinary multi-buy, not a pack whose count leaked — so a measure history must not raise the flag.
    private static bool UsuallyHasACountSize(IReadOnlyCollection<string?> priorSizes)
    {
        if (priorSizes.Count == 0) return false;
        var countSized = priorSizes.Count(IsCountSize);
        return countSized * 2 > priorSizes.Count;
    }
}
