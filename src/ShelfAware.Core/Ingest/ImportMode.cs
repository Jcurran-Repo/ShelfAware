namespace ShelfAware.Core.Ingest;

/// <summary>How an auto-imported receipt gets from "extracted" to "recorded".</summary>
public enum ImportMode
{
    /// <summary>Every import waits for human review on the Upload page.</summary>
    Review,

    /// <summary>Graduated trust (the default): a receipt auto-confirms only when EVERY line resolves
    /// to an already-known product via a learned alias or a high-confidence match. Anything uncertain —
    /// a new product, a shaky extraction — queues for review instead. Human attention goes exactly
    /// where the pipeline is unsure.</summary>
    Smart,

    /// <summary>Every import goes straight into history, new products included (the original
    /// all-or-nothing auto-confirm).</summary>
    Auto,
}

public static class ImportModes
{
    /// <summary>Parse the stored setting, honoring the legacy AutoConfirmImports bool from before the
    /// three-way mode existed (true → Auto, false → Review). Unset → Smart.</summary>
    public static ImportMode Parse(string? mode, string? legacyAutoConfirm)
    {
        if (Enum.TryParse<ImportMode>(mode, ignoreCase: true, out var parsed)) return parsed;
        if (bool.TryParse(legacyAutoConfirm, out var auto)) return auto ? ImportMode.Auto : ImportMode.Review;
        return ImportMode.Smart;
    }
}
