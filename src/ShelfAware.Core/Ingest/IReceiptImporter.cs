namespace ShelfAware.Core.Ingest;

/// <summary>
/// Scans the <see cref="IReceiptInbox"/> and auto-imports (extract → auto-confirm) every receipt file
/// not already imported. A Core port (like IPantryStore) so the chat/voice agent can trigger it without
/// depending on the Web/EF layer.
/// </summary>
public interface IReceiptImporter
{
    Task<ImportSummary> ImportNewAsync(
        IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Reported once per receipt as a scan works through the inbox, so a UI can show live progress
/// ("Reading receipt 2 of 5…") during a multi-receipt import instead of a blank spinner.</summary>
public record ImportProgress(int Current, int Total, string CurrentFile);

public record ImportSummary(bool Configured, int Imported, int Purchases, int NewProducts, int AwaitingReview, int Failed)
{
    public static ImportSummary NotConfigured { get; } = new(false, 0, 0, 0, 0, 0);

    /// <summary>One-line, speakable result for the chat/voice agent.</summary>
    public string Describe()
    {
        if (!Configured) return "No receipt folder is set up yet — add one on the Settings page.";

        var failedNote = Failed > 0 ? $" ({Failed} couldn't be read)" : "";
        if (AwaitingReview > 0)
            return $"Queued {AwaitingReview} receipt{Plural(AwaitingReview)} for you to review on the Upload page{failedNote}.";
        if (Imported > 0)
            return $"Imported {Imported} receipt{Plural(Imported)}: {Purchases} purchase{Plural(Purchases)}"
                + (NewProducts > 0 ? $", {NewProducts} new product{Plural(NewProducts)}" : "")
                + failedNote + ".";
        return Failed > 0 ? $"Couldn't read {Failed} receipt{Plural(Failed)}." : "No new receipts to import.";
    }

    private static string Plural(int n) => n == 1 ? "" : "s";
}
