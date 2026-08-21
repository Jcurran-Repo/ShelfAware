using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Extraction;

public record ExtractedReceipt
{
    public string? Merchant { get; init; }
    public DateOnly? PurchaseDate { get; init; }
    /// <summary>The receipt's printed SUBTOTAL (pre-tax), or null if not printed. A receipt-level
    /// figure read straight from the paper, distinct from the sum of the item <see cref="Lines"/>.</summary>
    public decimal? Subtotal { get; init; }
    /// <summary>The receipt's printed total TAX charged, or null if not printed.</summary>
    public decimal? Tax { get; init; }
    /// <summary>The receipt's printed final TOTAL / amount paid, or null if not printed.</summary>
    public decimal? Total { get; init; }
    /// <summary>The receipt's printed total savings/discounts (instant savings, coupons, member
    /// savings), or null if none is printed.</summary>
    public decimal? Savings { get; init; }
    public List<ExtractedLine> Lines { get; init; } = [];
}

public record ExtractedLine
{
    public required string RawText { get; init; }
    public required string NormalizedName { get; init; }
    public string? Brand { get; init; }
    public decimal Quantity { get; init; } = 1;
    public string? Size { get; init; }
    /// <summary>Flavor/varietal stripped from the item name (e.g. "Strawberry", "Gala"), or null.
    /// Like brand and size it is tracked per purchase, so flavors roll up into one product.</summary>
    public string? Variety { get; init; }
    public decimal? UnitPrice { get; init; }
    public Category Category { get; init; } = Category.Other;
    /// <summary>Descriptive tags the model suggests for this item (from the seed vocabulary), applied to
    /// the product on confirm. Empty when none apply.</summary>
    public string[] Tags { get; init; } = [];
    public decimal Confidence { get; init; }

    /// <summary>Exact name of an existing product the model judged this line to match, or null.
    /// Only set when a candidate product list is passed to extraction (LLM-assisted matching).</summary>
    public string? SuggestedProductName { get; init; }
}
