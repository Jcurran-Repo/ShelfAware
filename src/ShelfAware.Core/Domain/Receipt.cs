namespace ShelfAware.Core.Domain;

public class Receipt : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public string? Merchant { get; set; }
    public DateOnly? PurchasedAt { get; set; }
    public required string ImagePath { get; set; }
    public string RawModelJson { get; set; } = "";
    public ReceiptStatus Status { get; set; } = ReceiptStatus.PendingReview;
    /// <summary>HISTORICAL: the inbox file name this receipt was auto-imported from, back when the
    /// folder-import feature existed (retired 2026-07-22 — uploads are the one way in now). Nothing
    /// writes or reads it; the column stays because dropping one is a structural SQLite rebuild that
    /// pre-existing rows aren't worth.</summary>
    public string? SourceFile { get; set; }

    /// <summary>The user's explicit assertion that they checked every line, making this receipt's
    /// confirmed lines usable as extraction ground truth ("your receipts" on /accuracy). NEVER set by a
    /// machine confirm — an unreviewed receipt as "truth" would let the eval grade extraction against
    /// itself and inflate the scores.</summary>
    public bool VerifiedForEval { get; set; }

    /// <summary>When the confirm actually RAN — distinct from <see cref="PurchasedAt"/>, which is the
    /// date printed on the receipt. Exists so removal can order the confirm against a later human count:
    /// a count attested after this moment already reflects whatever the confirm put on the shelf, so
    /// removal must not subtract past it (§13.2). Null on receipts confirmed before the column existed —
    /// removal treats those as it always did. Stamped once, on the PendingReview → Confirmed transition
    /// only; a re-confirm is a no-op and must not move it.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>The receipt's OWN printed money figures, captured once at extraction — distinct from the
    /// line-item sum <see cref="ReceiptTotals"/> computes, which can differ by tax, per-unit rounding,
    /// and discount lines that are never stored as items. Null when the receipt didn't print the figure,
    /// or was recorded before totals were captured (2026-08-21). A record of what the paper said; nothing
    /// recomputes them. <see cref="Subtotal"/> is pre-tax, <see cref="Total"/> the amount paid, so
    /// Subtotal + <see cref="Tax"/> = Total on a well-formed receipt.</summary>
    public decimal? Subtotal { get; set; }
    public decimal? Tax { get; set; }
    public decimal? Total { get; set; }
    /// <summary>Total instant-savings / coupon / member discount printed on the receipt. Summed across a
    /// household's confirmed receipts for its running "amount saved". Null = none printed.</summary>
    public decimal? Savings { get; set; }

    public List<ReceiptLine> Lines { get; set; } = [];
}
