namespace ShelfAware.Core.Domain;

public class ProductAlias : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public required string Merchant { get; set; }
    public required string RawText { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>The receipt whose human confirm taught (or last RE-pointed) this pairing, or null
    /// (taught before 2026-07-22). Provenance for "remove this receipt": only the teacher's removal
    /// un-teaches the alias. A confirm that re-walks the pairing without changing it is not a new
    /// teacher — a duplicate upload must not inherit credit for a lesson an earlier receipt taught.
    /// A breadcrumb, not an FK (no navigation): the receipt may be gone while the alias lives on.</summary>
    public int? TaughtByReceiptId { get; set; }

    /// <summary>The human-corrected BRAND for this merchant's raw text, or null (never corrected). The
    /// alias fixes which PRODUCT an opaque line means, and the product's own name pre-fills the review
    /// (the product is brand-agnostic); brand is per-purchase, so there's nothing to derive it from —
    /// this remembers it, so a line read as one brand but actually another ("Dentastix" that's really
    /// "Dently's") pre-fills the right brand on the next receipt. Taught by human confirm only, last
    /// positive write wins; a blank brand left in review never erases one learned earlier.</summary>
    public string? LearnedBrand { get; set; }
}
