namespace ShelfAware.Core.Settings;

/// <summary>Runtime-editable app configuration surfaced on the Settings page. Key/value; small and
/// single-user, so it lives in the app's own DB rather than external config.</summary>
public interface IAppSettings
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every key the settings table can hold, and — just as importantly — whether each is CONFIGURATION or
/// the household's own CONTENT.
///
/// The distinction earns its keep at "delete my data": this table is mostly how the app is set up, but
/// some keys hold content derived from a household's pantry (their last recipe ideas; their receipts'
/// self-eval scores, merchant names and all), and that has to go when they delete their data.
///
/// Classify here rather than listing keys at the delete site — a test fails if a new key is in neither
/// list, so the choice gets made rather than defaulted to "survives".
/// </summary>
public static class SettingKeys
{
    /// <summary>Folder the assistant scans for new receipt files to auto-import.</summary>
    public const string ReceiptFolder = "ReceiptFolder";

    /// <summary>An <see cref="Ingest.ImportMode"/> name: Review, Smart (default), or Auto.</summary>
    public const string ImportMode = "ImportMode";

    /// <summary>LEGACY "true"/"false" from before the three-way <see cref="ImportMode"/> existed —
    /// still read as a fallback (true → Auto, false → Review) so an existing choice is honored.</summary>
    public const string AutoConfirmImports = "AutoConfirmImports";

    /// <summary>"Confirm" (default — the assistant asks before adding a recipe's ingredients to the grocery
    /// list) or "Auto" (add them straight away). The parallel-to-<see cref="ImportMode"/> setting for the
    /// add-a-recipe's-ingredients-to-the-list voice/chat flow.</summary>
    public const string RecipeAddConfirm = "RecipeAddConfirm";

    /// <summary>JSON snapshot of the household's most recent "Get ideas" batch (request + timestamp +
    /// suggestions), so an AI call's results survive navigation and restarts instead of evaporating.
    /// Replaced on the next batch, cleared by the user's "Clear ideas".</summary>
    public const string LastRecipeSuggestions = "LastRecipeSuggestions";

    /// <summary>JSON of the household's last receipt self-eval run (per-receipt scores, each named for the
    /// merchant and date it came from). Persisted so the Accuracy page can show the last run without
    /// re-spending a vision call per receipt.</summary>
    public const string SelfEvalResults = "SelfEvalResults";

    /// <summary>How the app is set up. Survives "delete my data": wiping your pantry shouldn't forget
    /// which folder your receipts arrive in.</summary>
    public static readonly IReadOnlyList<string> Config =
        [ReceiptFolder, ImportMode, AutoConfirmImports, RecipeAddConfirm];

    /// <summary>Derived from the household's own pantry and receipts, and therefore theirs: removed by
    /// "delete my data" like any other content.</summary>
    public static readonly IReadOnlyList<string> UserContent =
        [LastRecipeSuggestions, SelfEvalResults];
}
