namespace ShelfAware.Core.Domain;

/// <summary>
/// Grouping key for a purchase/receipt-line size string. Size is metadata, not identity — but when
/// the app DOES group by size (price series, price index, dominant-size cadence), the "sold per
/// each" family must land in one bucket: extraction writes loose produce inconsistently (usually
/// null, sometimes a literal "each"/"ea"/"1 ct"), and they're all the same buying basis. Anything
/// else groups by its trimmed, lowercased text. No unit arithmetic ("1 gal" ≠ 2 × "64 fl oz"), by
/// design — see the data-model notes in CLAUDE.md.
/// </summary>
public static class SizeBucket
{
    public const string EachKey = "each";

    /// <summary>The each-family collapses to <see cref="EachKey"/>; anything else groups by its
    /// trimmed, lowercased text. The spellings are matched in the method body (not held in a static
    /// set) deliberately: a static readonly collection's string literals are initialised once and
    /// cached, so mutation testing can never toggle them — inline, each spelling is a live, killable
    /// mutant pinned by a test. Case is folded to lowercase first, so the ordinal patterns below are
    /// exhaustive (every spelling is already lowercase).
    /// <para>⚠️ The literal "each" is deliberately NOT in the pattern: it already equals
    /// <see cref="EachKey"/>, so an input of "each" returns "each" through the <c>: s</c> fallback
    /// anyway. Listing it would add a mutation-equivalent no-op (Stryker can't narrowly suppress one
    /// literal in a multi-alternative pattern). Do not "restore" it. If <see cref="EachKey"/> ever
    /// stops being "each", this method must be revisited.</para></summary>
    public static string Key(string? size)
    {
        var s = (size ?? "").Trim().ToLowerInvariant();
        return s is "" or "ea" or "ea." or "per each" or "1 each" or "1 ct" or "1ct" or "loose" or "single"
            ? EachKey
            : s;
    }
}
