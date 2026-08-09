namespace ShelfAware.Core.Shopping;

/// <summary>
/// The dropdown label for a product whose name TWO OR MORE products share (a twin): the bare name is a
/// choice nobody can make, so the option carries the one fact that tells twins apart — the count each
/// holds. ONE definition because two review grids render it (the receipt upload's product-match dropdown
/// and the census grid's), and a phrasing rule copied per page drifts the first time one copy is edited.
/// Twins with the same count (or none) stay indistinguishable — the honest limit a dropdown can't beat.
/// </summary>
public static class ProductOptionLabel
{
    /// <param name="name">The product's name (the caller has already decided it is a twin).</param>
    /// <param name="onHand">The product's stored count, or null when it has never been counted.</param>
    /// <param name="counting">Whether the count is live (<c>TrackQuantity</c>) — a dormant number is
    /// kept history (§13.3), and the label says so rather than presenting it as current stock.</param>
    /// <param name="unit">The product's display unit, if any (<c>DefaultUnit</c>).</param>
    public static string ForTwin(string name, decimal? onHand, bool counting, string? unit) =>
        onHand is { } quantity
            ? counting
                ? $"{name} — {QuantityFormat.Describe(quantity, unit)} on hand"
                : $"{name} — had {QuantityFormat.Describe(quantity, unit)}, counting stopped"
            : $"{name} — not counted";
}
