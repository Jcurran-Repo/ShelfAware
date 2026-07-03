namespace ShelfAware.Core.Ingest;

/// <summary>
/// Scans the <see cref="IReceiptInbox"/> and auto-imports (extract → auto-confirm) every receipt file
/// not already imported. A Core port (like IPantryStore) so the chat/voice agent can trigger it without
/// depending on the Web/EF layer.
/// </summary>
public interface IReceiptImporter
{
    Task<ImportSummary> ImportNewAsync(CancellationToken cancellationToken = default);
}

public record ImportSummary(bool Configured, int Imported, int Purchases, int NewProducts, int Failed)
{
    public static ImportSummary NotConfigured { get; } = new(false, 0, 0, 0, 0);

    /// <summary>One-line, speakable result for the chat/voice agent.</summary>
    public string Describe() =>
        !Configured
            ? "No receipt folder is set up yet — add one on the Settings page."
            : Imported == 0
                ? "No new receipts to import."
                : $"Imported {Imported} receipt{Plural(Imported)}: {Purchases} purchase{Plural(Purchases)}"
                    + (NewProducts > 0 ? $", {NewProducts} new product{Plural(NewProducts)}" : "")
                    + (Failed > 0 ? $" ({Failed} couldn't be read)" : "")
                    + ".";

    private static string Plural(int n) => n == 1 ? "" : "s";
}
