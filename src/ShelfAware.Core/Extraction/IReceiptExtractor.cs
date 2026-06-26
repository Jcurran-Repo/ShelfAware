namespace ShelfAware.Core.Extraction;

/// <summary>
/// Extracts structured purchase data from receipt attachments (photos,
/// digital-order screenshots, or print-to-PDF order pages). One call covers
/// one receipt, which may span multiple attachments.
/// </summary>
public interface IReceiptExtractor
{
    /// <param name="knownProductNames">Existing product names the model may match lines against
    /// (LLM-assisted matching). When null/empty, no matching is attempted and
    /// <see cref="ExtractedLine.SuggestedProductName"/> is always null.</param>
    Task<ExtractionResult> ExtractAsync(
        IReadOnlyList<ReceiptAttachment> attachments,
        IReadOnlyList<string>? knownProductNames = null,
        CancellationToken cancellationToken = default);
}

/// <param name="MediaType">MIME type: image/jpeg, image/png, image/gif, image/webp, or application/pdf.</param>
public record ReceiptAttachment(byte[] Data, string MediaType);

public record ExtractionResult
{
    public bool Success { get; init; }
    public ExtractedReceipt? Receipt { get; init; }
    /// <summary>Raw model output, kept for audit/debug regardless of success.</summary>
    public string RawModelJson { get; init; } = "";
    public string? Error { get; init; }

    public static ExtractionResult Ok(ExtractedReceipt receipt, string rawJson) =>
        new() { Success = true, Receipt = receipt, RawModelJson = rawJson };

    public static ExtractionResult Fail(string error, string rawJson = "") =>
        new() { Success = false, Error = error, RawModelJson = rawJson };
}
