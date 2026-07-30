namespace ShelfAware.Core.Shopping;

/// <summary>
/// How much of something, written for a reader — the ONE place, so every surface that shows a quantity
/// says it the same way.
/// <para>The problem it solves: a quantity carries no unit of its own. <c>PurchaseEvent.Quantity</c> is a
/// count of packages for a counted item and a WEIGHT for a weight item (2.34 lb of beef is one package,
/// not 2.34 of anything), and only <see cref="Domain.Product.DefaultUnit"/> says which. So: label it when
/// the product declares a unit, and print a bare number when it doesn't. <b>Never assume "packages"</b> —
/// most products have no unit set, and "2.34 packages" of ground beef is a confident lie where "2.34" is
/// merely incomplete.</para>
/// </summary>
public static class QuantityFormat
{
    /// <summary>"2.34 lb" when the product declares a unit, "4" when it doesn't. Trailing zeros are
    /// dropped either way, so a whole count reads "4" and not "4.00".
    /// <para><c>0.##</c>, matching the recommended-quantity displays on Products and Product Detail —
    /// and NOT <c>0.#</c>, which silently rounds a 2.34 lb pack of beef to "2.3" and loses precision on
    /// exactly the weight items this exists for.</para>
    /// <para>Exactly 1 of a plural unit drops the "s" — "1 can", not "1 cans". Units are human-typed
    /// free text ("cans", "jars", "lb", "each"), so this is a plain English trim: a single trailing
    /// "s" not preceded by another ("glass" keeps its name). Naive on purpose — the app is
    /// English-only and a unit box invites plurals, which read wrong beside a 1 the moment the count
    /// gets there.</para></summary>
    public static string Describe(decimal quantity, string? defaultUnit)
    {
        var number = quantity.ToString("0.##");
        if (string.IsNullOrWhiteSpace(defaultUnit)) return number;
        var unit = defaultUnit.Trim();
        if (quantity == 1m && unit.Length > 1
            && unit.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && !unit.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
        {
            unit = unit[..^1]; // case-insensitive detection, case-preserving trim: "Cans" → "Can"
        }
        return $"{number} {unit}";
    }
}
