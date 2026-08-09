namespace ShelfAware.Core.Census;

/// <summary>
/// Reads a photograph of a shelf, freezer, or cupboard and PROPOSES what is on it (DESIGN.md §13.8).
/// The intake answer for stock a receipt can never know about — bought pre-app, bought elsewhere,
/// gifted, bulk.
/// <para>It proposes; it never records. What comes back is a candidate list a human corrects, and the
/// writing is <c>CensusConfirmationService</c>'s job — deliberately not the receipt confirm path, because
/// a census must never create a <c>PurchaseEvent</c> (§13.8's ★ rule: you did not buy those today, and
/// invented purchases would poison every rhythm in the app).</para>
/// </summary>
public interface IShelfCensusReader
{
    /// <param name="photos">One or more photos of DIFFERENT parts of the same storage space. Read as one
    /// census and merged into a single item list.</param>
    /// <param name="knownProductNames">Existing product names the reader may match items against, so a
    /// census lands on the product a household already has rather than a twin of it. When null/empty,
    /// <see cref="CensusItem.SuggestedProductName"/> is always null.</param>
    Task<ShelfCensusResult> ReadAsync(
        IReadOnlyList<ShelfPhoto> photos,
        IReadOnlyList<string>? knownProductNames = null,
        CancellationToken cancellationToken = default);
}

/// <summary>One photo of a shelf. Images only — deliberately narrower than a receipt attachment, which
/// also takes PDFs: nobody prints a freezer to PDF, and a narrower contract is one less thing to handle.</summary>
/// <param name="MediaType">MIME type: image/jpeg, image/png, image/gif, or image/webp.</param>
public record ShelfPhoto(byte[] Data, string MediaType);

public record ShelfCensusResult
{
    public bool Success { get; init; }
    public IReadOnlyList<CensusItem> Items { get; init; } = [];
    /// <summary>Raw model output, kept for debugging regardless of success. NOT persisted — a census is
    /// read, reviewed, and confirmed in one sitting, and the photo of someone's freezer never lands on
    /// disk (see the page).</summary>
    public string RawModelJson { get; init; } = "";
    public string? Error { get; init; }

    public static ShelfCensusResult Ok(IReadOnlyList<CensusItem> items, string rawJson) =>
        new() { Success = true, Items = items, RawModelJson = rawJson };

    public static ShelfCensusResult Fail(string error, string rawJson = "") =>
        new() { Success = false, Error = error, RawModelJson = rawJson };
}
