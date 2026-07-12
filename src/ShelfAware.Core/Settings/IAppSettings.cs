namespace ShelfAware.Core.Settings;

/// <summary>Runtime-editable app configuration surfaced on the Settings page. Key/value; small and
/// single-user, so it lives in the app's own DB rather than external config.</summary>
public interface IAppSettings
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

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
}
