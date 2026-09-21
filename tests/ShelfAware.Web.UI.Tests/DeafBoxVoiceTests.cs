using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Components;
using ShelfAware.Web.Components.Layout;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// A box with no ear offers no microphone — at EVERY site that offers one.
///
/// <para>The demo box going managed with no ElevenLabs key (2026-09-21) made this the default
/// experience rather than a corner: the three mic affordances gated on a browser-support check alone,
/// so a visitor could press one, record a whole sentence, and be told to add a key in a Settings panel
/// that a managed deployment hides. These tests are per SITE on purpose — <c>VoiceEarTests</c> pins the
/// rule, and this file pins that every caller actually asks it. A half-converted state (one control
/// standing down while its neighbour still promises to listen) is the failure CLAUDE.md warns about,
/// and it is exactly what a rule-only test would let through.</para>
/// </summary>
public class DeafBoxVoiceTests : VoiceTestBase
{
    /// <summary>The demo box's shape: host keys authoritative, and the host configured no voice key.</summary>
    private void MakeBoxDeaf()
    {
        Voice.ApiKey = "";
        Voice.Managed = true;
    }

    [Fact]
    public void The_roaming_assistant_is_not_offered()
    {
        MakeBoxDeaf();
        JSInterop.SetupModule("/js/conversation.js").Setup<bool>("isSupported").SetResult(true);

        var cut = Render<VoiceAgent>();

        Assert.DoesNotContain("voice-agent-fab", cut.Markup);
        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void Push_to_talk_is_not_offered()
    {
        MakeBoxDeaf();
        JSInterop.SetupModule("/js/voice.js").Setup<bool>("isSupported").SetResult(true);

        var cut = Render<PushToTalk>();

        Assert.DoesNotContain("mic-label", cut.Markup);
        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void The_hands_free_reader_says_why_rather_than_reaching_for_the_microphone()
    {
        MakeBoxDeaf();
        SeedRecipe();
        JSInterop.SetupModule("/js/cooklisten.js").Setup<bool>("isSupported").SetResult(true);
        JSInterop.SetupModule("/js/voice.js");
        JSInterop.SetupModule("/js/reader.js");

        var cut = Render<RecipeReadAloud>(p => p
            .Add(c => c.Recipe, TheRecipe())
            .Add(c => c.HandsFree, true));

        cut.WaitForAssertion(() =>
            Assert.Contains("Voice input isn't available on this box.", cut.Markup));
        // The reader still reads — losing the ear costs the mic, not the recipe.
        Assert.Contains("Use the buttons above.", cut.Markup);
        // ⚠️ Never ask a managed visitor for a key they cannot supply.
        Assert.DoesNotContain("key in Settings", cut.Markup);
        // And it never asked the browser for the microphone at all.
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "startSession");
    }

    [Fact]
    public void Recipes_offers_the_read_instead_of_a_cook_along_it_cannot_run()
    {
        MakeBoxDeaf();
        SeedRecipe();

        var cut = Render<Recipes>();
        cut.WaitForState(() => cut.FindAll(".saved-recipes li").Count > 0);

        Assert.DoesNotContain("Cook-along", cut.Markup);
        Assert.Contains("Read it to me", cut.Markup);
    }

    [Fact]
    public void A_local_ear_offers_everything_on_a_box_with_no_key_at_all()
    {
        // ⚠️ The demo box after Speech:Ear=Moonshine: managed, keyless, and fully able to listen — a
        // model in this process is nobody's credential. This is the case that fails if anyone
        // "simplifies" the gate back to asking about an API key.
        Voice.ApiKey = "";
        Voice.Managed = true;
        Voice.LocalEar = true;
        SeedRecipe();
        JSInterop.SetupModule("/js/voice.js").Setup<bool>("isSupported").SetResult(true);

        var mic = Render<PushToTalk>();
        Assert.Contains("mic-label", mic.Markup);

        var cut = Render<Recipes>();
        cut.WaitForState(() => cut.FindAll(".saved-recipes li").Count > 0);
        Assert.Contains("Cook-along", cut.Markup);
    }

    [Fact]
    public void A_box_that_can_hear_still_offers_all_of_it()
    {
        // The other half of the gate: these assertions are what fails if someone "fixes" a deaf box by
        // hiding the microphone everywhere, which would cost the family box and every self-host their
        // voice. Voice.ApiKey is the harness default, so this is the ordinary deployment.
        SeedRecipe();
        JSInterop.SetupModule("/js/voice.js").Setup<bool>("isSupported").SetResult(true);

        var mic = Render<PushToTalk>();
        Assert.Contains("mic-label", mic.Markup);

        var cut = Render<Recipes>();
        cut.WaitForState(() => cut.FindAll(".saved-recipes li").Count > 0);
        Assert.Contains("Cook-along", cut.Markup);
    }

    private void SeedRecipe()
    {
        using var db = Db.CreateDbContext();
        db.Recipes.Add(new Recipe
        {
            Name = "Chilli",
            SavedAt = DateTimeOffset.Now,
            Ingredients = [new RecipeIngredient { Name = "beef", IsMain = true }],
            Steps = [new RecipeStep { Order = 1, Text = "Brown the beef." }],
        });
        db.SaveChanges();
    }

    private Recipe TheRecipe()
    {
        using var db = Db.CreateDbContext();
        return db.Recipes.Single();
    }
}
