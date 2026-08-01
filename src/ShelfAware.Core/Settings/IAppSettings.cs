namespace ShelfAware.Core.Settings;

/// <summary>Runtime-editable app configuration surfaced on the Settings page. Key/value; small and
/// single-user, so it lives in the app's own DB rather than external config.</summary>
public interface IAppSettings
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every key the settings table can hold, documented for what it MEANS.
///
/// Deliberately no config-vs-content classification: "delete my data" removes a household's settings
/// rows wholesale, so there is no per-key choice to make and no second list that could fall out of step
/// with these constants. Every key below has a sensible default when absent, which is what makes wiping
/// them safe rather than merely tidy.
/// </summary>
public static class SettingKeys
{
    /// <summary>An <see cref="Ingest.ImportMode"/> name: Review, Smart (default), or Auto — how an
    /// uploaded receipt gets from "extracted" to "recorded". (Rows keyed "ReceiptFolder" may linger in
    /// older DBs from the retired folder-import feature; nothing reads them.)</summary>
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

    /// <summary>"true" to track expiration dates: the review screen gains an optional per-line date, the
    /// product page gains an expiration panel, and a passed date marks the item out (a "timed OutNow").
    /// Absent/other = OFF (the default — it's the most ritual-heavy field in the app, so households opt
    /// in). Off is DORMANT, not destructive: recorded dates are kept but never fire and never render.
    /// One definition of "on": <see cref="AppSettingsExtensions.GetTrackExpirationDatesAsync"/>.</summary>
    public const string TrackExpirationDates = "TrackExpirationDates";
}

public static class AppSettingsExtensions
{
    /// <summary>THE definition of "expiration tracking is on" for this household — every page, the chat
    /// tools, and the recipe adapter ask this one method, so the toggle can't half-apply. Absent or
    /// anything but "true" = off.</summary>
    public static async Task<bool> GetTrackExpirationDatesAsync(this IAppSettings settings, CancellationToken cancellationToken = default) =>
        string.Equals(await settings.GetAsync(SettingKeys.TrackExpirationDates, cancellationToken), "true", StringComparison.OrdinalIgnoreCase);
}
