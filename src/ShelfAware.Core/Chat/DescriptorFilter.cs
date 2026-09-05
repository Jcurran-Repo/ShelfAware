namespace ShelfAware.Core.Chat;

/// <summary>
/// The throwaway words a product name can shed without changing WHICH item it is — pure manner/marketing
/// filler that never names a distinct food ("Greek <b>Style</b> Yogurt" is Greek Yogurt; "store
/// <b>brand</b> ketchup" is ketchup). Shed inside <see cref="ProductMatcher"/>'s FUZZY token overlap (and
/// its IDF), so a filler-only difference reliably reads as a NEAR-MISS the add / census surfaces ADVISE on
/// ("looks like you already have Greek Yogurt — use it or add anyway") instead of splitting one item into
/// two artificially-separate products.
///
/// <para>⚠️ Deliberately feeds the ADVISORY path, NOT product IDENTITY (<see cref="ProductMatcher.IdentityKey"/>
/// is untouched). That is the crucial safety choice: identity is what the duplicate guard blocks an add on
/// OUTRIGHT and what the census/rename treat as "the same product", so a wrong strip there would silently
/// BLOCK a legitimate product or auto-merge two real ones — the item-41 blast radius. Routed through the
/// fuzzy near-miss instead, a wrong strip is recoverable with one "add anyway" — far lower stakes.</para>
///
/// <para>It is still kept TINY and conservative (a needless advisory is friction). The bar to add a word:
/// it has NO food-distinguishing meaning in ANY context (a manner/marketing word, never a type / cut /
/// form / flavour / section) — and the direct <c>DescriptorFilter</c> test pins the membership so adding
/// one is a deliberate, reviewed act. Aisle/section words (Bakery, Deli) and form words (Frozen, Fresh,
/// Loaf) are NOT here — each can name a genuinely distinct item, and the "these two look like the same
/// food" cases are the grocery-list nudge's job.</para>
/// </summary>
public static class DescriptorFilter
{
    // Tokens arrive already lowercased and punctuation-folded from Normalize, so Ordinal is exact here.
    private static readonly HashSet<string> Throwaway = new(StringComparer.Ordinal)
    {
        "style", // a manner ("Greek style yogurt" ≡ "Greek yogurt") — never the food itself
        "brand", // "store brand ketchup" ≡ "ketchup"; the bare word "brand" never names a food
    };

    /// <summary>Whether a single normalized token is throwaway filler.</summary>
    public static bool IsThrowaway(string token) => Throwaway.Contains(token);
}
