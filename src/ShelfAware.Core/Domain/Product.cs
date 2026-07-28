namespace ShelfAware.Core.Domain;

public class Product : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public required string Name { get; set; }
    /// <summary>The store aisle where you'd grab this item — drives grocery-list ordering. The single
    /// primary axis; finer descriptors live in <see cref="Tags"/>.</summary>
    public Category Category { get; set; }
    public string? DefaultUnit { get; set; }
    public bool IsTracked { get; set; } = true;

    /// <summary>Whether this household counts this item (DESIGN.md §13). Opt-in per product and OFF by
    /// default: the hoard is a few dozen items — freezer meat, canned goods, bulk — and everything else
    /// keeps running on the learned cadence alone. A feature that demanded you count the salt would be
    /// abandoned in a week, so false must mean "behave exactly as before".</summary>
    public bool TrackQuantity { get; set; }

    /// <summary>How much is on hand, or null when unknown. PACKAGES for a counted item and the item's own
    /// unit for a weight item — see <see cref="Shopping.QuantityFormat"/>, which is the one place that
    /// decides how to say it. Deliberately not normalized against <see cref="Size"/>-style volumes: four
    /// milk is four jugs, two of which may be gallons (the standing no-unit-arithmetic decision).</summary>
    public decimal? QuantityOnHand { get; set; }

    /// <summary>When a HUMAN last vouched for <see cref="QuantityOnHand"/> — setting it, confirming a
    /// prompt, correcting a decrement. Automated movement (a receipt's +N, an "Ate it" −1) changes the
    /// number and leaves this date ALONE, and that gap is the whole point: it's what lets the engine ask
    /// "you counted 3 in March and one usually lasts 9 days — still got them?" instead of trusting a
    /// count forever. A last-modified stamp would answer a different question and detect nothing.</summary>
    public DateTimeOffset? QuantityCountedAt { get; set; }
    /// <summary>The receipt whose confirm CREATED this product, or null (created by hand, by the demo
    /// seeder, or before 2026-07-22). Provenance for "remove this receipt": a product the receipt
    /// introduced — and that gathered no other history since — goes with it. A plain breadcrumb, not
    /// an FK (no navigation): the receipt may be long gone while the product lives on.</summary>
    public int? CreatedByReceiptId { get; set; }

    public List<PurchaseEvent> Purchases { get; set; } = [];
    public List<InventorySignal> Signals { get; set; } = [];
    /// <summary>Descriptive tags (Condiment, Canned, Snack, …) — the browsable second category layer.</summary>
    public List<ProductTag> Tags { get; set; } = [];
    /// <summary>Recipe ingredients this product can stand in for ("also works as") — drives makeability
    /// without genericizing recipes. AI-seeded, user-curated.</summary>
    public List<ProductSubstitute> Substitutes { get; set; } = [];
}
