using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Extraction;

public record ExtractedReceipt
{
    public string? Merchant { get; init; }
    public DateOnly? PurchaseDate { get; init; }
    public List<ExtractedLine> Lines { get; init; } = [];
}

public record ExtractedLine
{
    public required string RawText { get; init; }
    public required string NormalizedName { get; init; }
    public string? Brand { get; init; }
    public decimal Quantity { get; init; } = 1;
    public string? Size { get; init; }
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
