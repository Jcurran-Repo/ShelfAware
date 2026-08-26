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

        // A count-shaped quantity with NO size, on a product usually sold in a pack of EXACTLY this many —
        // a prior COUNT size whose own count equals this quantity, so the pack count likely leaked into
        // quantity. ⚠️ Matching a prior PACK COUNT, not merely "usually count-sized", is what separates the
        // leak (a 12-roll pack read as qty 12) from a genuine stock-up (four 12-ct cartons — qty 4 matches
        // no prior count of 4, so it isn't nagged).
        if (string.IsNullOrWhiteSpace(lineSize) && MatchesAUsualPackCount(quantity, priorSizes))
            return QuantityFlag.MissingUsualSize;

        return QuantityFlag.None;
    }

    // Weight/volume units — a size carrying one is a per-unit MEASURE. A closed set for the grocery
    // domain (length units like "in"/"ft" essentially never size a grocery item).
    private static readonly HashSet<string> MeasureUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "oz", "ounce", "ounces", "lb", "lbs", "pound", "pounds", "g", "gr", "gram", "grams", "kg", "mg",
        "ml", "cl", "dl", "l", "liter", "liters", "litre", "litres", "gal", "gallon", "gallons",
        "qt", "quart", "quarts", "pt", "pint", "pints", "floz", "fl", "cup", "cups", "tbsp", "tbs", "tsp", "cc",
    };

    // Pack/count indicators — a size carrying one is sold BY COUNT (a pack), so its LEADING number is a
    // pack count. Deliberately unambiguous pack words only, not container words (bar/can/box/jar), which
    // are as often the item's own vessel as a pack ("12 oz bar" is one bar, not a 12-pack).
    private static readonly HashSet<string> CountUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "ct", "count", "cnt", "pk", "pack", "packs", "pkg", "pkgs", "x",
        "roll", "rolls", "eggs", "dozen", "doz", "ea", "each", "pod", "pods",
        "sheet", "sheets", "load", "loads", "wipe", "wipes", "tablet", "tablets", "capsule", "capsules",
    };

    // A size whose LEADING number is a pack count ("12 ct", "6 Mega Roll", "24pk", "24 pk 12 fl oz",
    // "12 x 12 oz", a bare "12") rather than a per-unit weight/volume ("12 oz") — the only kind whose
    // number matching the quantity points to a pack-count misread. ⚠️ A pack/count token ANYWHERE means
    // the leading number is the pack count even in a compound multipack with a trailing measure ("24 pk
    // 12 fl oz"): those — beverages, canned goods — are the MOST common packs, and an earlier "any
    // measure token → not a count" rule wrongly re-admitted the very bug for them. Reads LETTER runs, so
    // a space-less "12oz"/"24pk" is classed the same as "12 oz"/"24 pk".
    private static bool IsCountSize(string? size)
    {
        if (LeadingCount(size) is null) return false; // must start with a number to be a count at all
        var units = Regex.Matches(size!, "[A-Za-z]+").Select(m => m.Value).ToList();
        if (units.Any(CountUnits.Contains)) return true;    // a pack token → the leading number is a count
        if (units.Any(MeasureUnits.Contains)) return false; // purely a measure → the leading number is it
        return true; // a bare number ("12") or an unrecognised unit — lean count
    }

    // The quantity equals a pack count this product has actually been sold in — a prior COUNT size whose
    // own leading count is exactly this quantity.
    private static bool MatchesAUsualPackCount(decimal quantity, IReadOnlyCollection<string?> priorSizes) =>
        priorSizes.Any(s => IsCountSize(s) && LeadingCount(s) == quantity);

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
}
