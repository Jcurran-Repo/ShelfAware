using ShelfAware.Core.Census;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Evaluation;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Speech;

namespace ShelfAware.Tests;

/// <summary>
/// Covers the plain result/DTO shapes the interfaces expose — the Ok/Fail factories, their defaulted
/// fields, and the derived properties. These carry no branching worth a dedicated suite, but their
/// success booleans and default strings are real mutants: a factory that returned the wrong Success, or
/// dropped an empty-string default, would flip a caller's whole control flow. One file so the pattern is
/// visible rather than scattered a mutant at a time.
/// </summary>
public class ResultDtoTests
{
    // ---- ChatResult ----

    [Fact]
    public void ChatResult_Ok_succeeds_and_defaults_the_navigation_fields()
    {
        var r = ChatResult.Ok("done", new[] { "a1", "a2" });

        Assert.True(r.Success);
        Assert.Equal("done", r.Reply);
        Assert.Equal(new[] { "a1", "a2" }, r.Actions);
        Assert.Null(r.NavigateTo);
        Assert.False(r.HandsOff);
        Assert.Null(r.StepTarget);
    }

    [Fact]
    public void ChatResult_Ok_carries_explicit_navigation_fields()
    {
        var r = ChatResult.Ok("go", [], navigateTo: "/recipes?read=12", handsOff: true, stepTarget: 3);

        Assert.Equal("/recipes?read=12", r.NavigateTo);
        Assert.True(r.HandsOff);
        Assert.Equal(3, r.StepTarget);
    }

    [Fact]
    public void ChatResult_Fail_is_unsuccessful_with_no_actions()
    {
        var r = ChatResult.Fail("nope");

        Assert.False(r.Success);
        Assert.Equal("nope", r.Reply);
        Assert.Empty(r.Actions);
        Assert.Null(r.NavigateTo);
        Assert.False(r.HandsOff);
        Assert.Null(r.StepTarget);
    }

    // ---- ExtractionResult ----

    [Fact]
    public void ExtractionResult_Ok_carries_the_receipt_and_raw_json()
    {
        var receipt = new ExtractedReceipt { Merchant = "Walmart" };
        var r = ExtractionResult.Ok(receipt, "{\"raw\":1}");

        Assert.True(r.Success);
        Assert.Same(receipt, r.Receipt);
        Assert.Equal("{\"raw\":1}", r.RawModelJson);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ExtractionResult_Fail_has_no_receipt_and_defaults_raw_json_to_empty()
    {
        var r = ExtractionResult.Fail("bad image");

        Assert.False(r.Success);
        Assert.Null(r.Receipt);
        Assert.Equal("bad image", r.Error);
        Assert.Equal("", r.RawModelJson);
    }

    [Fact]
    public void ExtractionResult_Fail_can_keep_the_raw_json()
    {
        var r = ExtractionResult.Fail("schema", "{\"partial\":true}");

        Assert.False(r.Success);
        Assert.Equal("{\"partial\":true}", r.RawModelJson);
    }

    [Fact]
    public void ExtractionResult_defaults_raw_json_to_empty_not_null()
    {
        // The factories always set RawModelJson, so the property initializer is only observable through a
        // bare construction — but it's the guarantee that RawModelJson is never null for a caller/audit.
        Assert.Equal("", new ExtractionResult().RawModelJson);
    }

    // ---- SpeechToTextResult ----

    [Fact]
    public void SpeechToTextResult_Ok_and_Fail()
    {
        var ok = SpeechToTextResult.Ok("next step");
        Assert.True(ok.Success);
        Assert.Equal("next step", ok.Text);
        Assert.Null(ok.Error);

        var fail = SpeechToTextResult.Fail("no audio");
        Assert.False(fail.Success);
        Assert.Equal("", fail.Text); // empty transcript on failure, never null
        Assert.Equal("no audio", fail.Error);
    }

    // ---- TextToSpeechResult ----

    [Fact]
    public void TextToSpeechResult_Ok_and_Fail()
    {
        var audio = new byte[] { 1, 2, 3 };
        var ok = TextToSpeechResult.Ok(audio, "audio/mpeg");
        Assert.True(ok.Success);
        Assert.Same(audio, ok.Audio);
        Assert.Equal("audio/mpeg", ok.MediaType);
        Assert.Null(ok.Error);

        var fail = TextToSpeechResult.Fail("no key");
        Assert.False(fail.Success);
        Assert.Empty(fail.Audio);       // empty clip on failure
        Assert.Equal("", fail.MediaType);
        Assert.Equal("no key", fail.Error);
    }

    // ---- RecipeImportResult ----

    [Fact]
    public void RecipeImportResult_Ok_and_Fail()
    {
        var recipe = new ImportedRecipe("Pasta", null, null, [], [], []);
        var ok = RecipeImportResult.Ok(recipe);
        Assert.True(ok.Success);
        Assert.Same(recipe, ok.Recipe);
        Assert.Null(ok.Error);

        var fail = RecipeImportResult.Fail("no recipe found");
        Assert.False(fail.Success);
        Assert.Null(fail.Recipe);
        Assert.Equal("no recipe found", fail.Error);
    }

    // ---- ShelfCensusResult ----

    [Fact]
    public void ShelfCensusResult_Ok_carries_items_and_raw_json()
    {
        var items = new[]
        {
            new CensusItem { NormalizedName = "Beans", Evidence = CensusEvidence.Label, VisibleCount = 3 },
        };
        var r = ShelfCensusResult.Ok(items, "{\"raw\":1}");

        Assert.True(r.Success);
        Assert.Same(items, r.Items);
        Assert.Equal("{\"raw\":1}", r.RawModelJson);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ShelfCensusResult_Fail_has_no_items_and_defaults_raw_json_to_empty()
    {
        var r = ShelfCensusResult.Fail("unreadable");

        Assert.False(r.Success);
        Assert.Empty(r.Items);
        Assert.Equal("unreadable", r.Error);
        Assert.Equal("", r.RawModelJson);
    }

    [Fact]
    public void ShelfCensusResult_defaults_its_collections_and_raw_json()
    {
        // Bare construction observes the property initializers the factories override — Items is never
        // null (a caller can enumerate it) and RawModelJson is never null.
        var bare = new ShelfCensusResult();
        Assert.Empty(bare.Items);
        Assert.Equal("", bare.RawModelJson);
    }

    // ---- RecipeSuggestion / SuggestedIngredient derived properties ----

    [Fact]
    public void SuggestedIngredient_Have_is_true_only_with_a_matched_product()
    {
        Assert.True(new SuggestedIngredient("Chicken", IsMain: true, MatchedProduct: "Chicken Breast").Have);
        Assert.False(new SuggestedIngredient("Chicken", IsMain: true, MatchedProduct: null).Have);
    }

    [Fact]
    public void RecipeSuggestion_ToGrab_is_the_main_ingredients_not_on_hand()
    {
        var suggestion = new RecipeSuggestion("Stir fry", "quick", new[]
        {
            new SuggestedIngredient("Chicken", IsMain: true, MatchedProduct: "Chicken Breast"), // main + have -> no
            new SuggestedIngredient("Broccoli", IsMain: true, MatchedProduct: null),            // main + need -> yes
            new SuggestedIngredient("Soy Sauce", IsMain: false, MatchedProduct: null),          // seasoning -> no
        }, []);

        Assert.Equal(new[] { "Broccoli" }, suggestion.ToGrab.Select(i => i.Name));
    }

    // ---- AppSetting defaults + the IHouseholdOwned setter ----

    [Fact]
    public void AppSetting_defaults_household_and_value_to_empty_not_null()
    {
        var s = new AppSetting { Key = "ImportMode" };
        Assert.Equal("", s.HouseholdId);
        Assert.Equal("", s.Value);
    }

    [Fact]
    public void AppSetting_household_setter_coalesces_null_to_empty()
    {
        var s = new AppSetting { Key = "ImportMode" };
        IHouseholdOwned owned = s;

        owned.HouseholdId = "H1";
        Assert.Equal("H1", s.HouseholdId);

        owned.HouseholdId = null;      // a NULL PK is exactly what the "" default guards against
        Assert.Equal("", s.HouseholdId);
    }

    // ---- BugReport derived states ----

    [Fact]
    public void BugReport_Resolved_tracks_the_resolved_stamp()
    {
        Assert.False(new BugReport { Body = "x" }.Resolved);
        Assert.True(new BugReport { Body = "x", ResolvedAt = DateTimeOffset.UnixEpoch }.Resolved);
    }

    [Fact]
    public void BugReport_AwaitingReporter_is_proposed_but_not_yet_resolved()
    {
        var proposed = new BugReport { Body = "x", ProposedResolvedAt = DateTimeOffset.UnixEpoch };
        Assert.True(proposed.AwaitingReporter);

        // a resolve leaves any lingering proposal moot
        var resolved = new BugReport
        {
            Body = "x", ProposedResolvedAt = DateTimeOffset.UnixEpoch, ResolvedAt = DateTimeOffset.UnixEpoch,
        };
        Assert.False(resolved.AwaitingReporter);

        Assert.False(new BugReport { Body = "x" }.AwaitingReporter); // never proposed
    }

    // ---- Receipt / EvalResults defaults ----

    [Fact]
    public void Receipt_defaults_raw_model_json_to_empty()
    {
        Assert.Equal("", new Receipt { ImagePath = "p" }.RawModelJson);
    }

    [Fact]
    public void EvalResults_and_FixtureScore_default_their_names_to_empty()
    {
        Assert.Equal("", new EvalResults().Model);
        Assert.Equal("", new FixtureScore().Name);
    }

    // ---- IAppSettings.GetTrackExpirationDatesAsync ----

    [Fact]
    public async Task GetTrackExpirationDatesAsync_is_on_only_for_true_under_the_right_key()
    {
        // Literal key in the fake (not SettingKeys.TrackExpirationDates) so the const's own string mutant
        // is observable: if the extension reads the wrong key, the fake returns null and the flag reads off.
        IAppSettings on = new KeyedSettings("TrackExpirationDates", "true");
        IAppSettings off = new KeyedSettings("TrackExpirationDates", "false");
        IAppSettings otherKey = new KeyedSettings("SomethingElse", "true");

        Assert.True(await on.GetTrackExpirationDatesAsync());
        Assert.False(await off.GetTrackExpirationDatesAsync());     // "true" literal must be exactly "true"
        Assert.False(await otherKey.GetTrackExpirationDatesAsync()); // read under the wrong key -> off
    }

    [Fact]
    public async Task GetTrackExpirationDatesAsync_ignores_case()
    {
        IAppSettings on = new KeyedSettings("TrackExpirationDates", "TRUE");
        Assert.True(await on.GetTrackExpirationDatesAsync()); // OrdinalIgnoreCase
    }

    private sealed class KeyedSettings(string key, string? value) : IAppSettings
    {
        public Task<string?> GetAsync(string k, CancellationToken cancellationToken = default) =>
            Task.FromResult(k == key ? value : null);

        public Task SetAsync(string k, string? v, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
