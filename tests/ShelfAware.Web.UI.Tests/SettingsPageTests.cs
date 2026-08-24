using System.Security.Claims;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Identity;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Settings;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Components.Pages;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;
using ShelfAware.Web.Tests;

namespace ShelfAware.Web.UI.Tests;

/// <summary>Shared wiring for the Settings page: the real household service (real UserManager —
/// removal is only removal because it bumps the security stamp), the real usage meter, and the
/// real delete-my-data service. Derivatives choose the server's LLM config.</summary>
public abstract class SettingsTestBase : PageTestContext
{

    private readonly string dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-ui-tests", Guid.NewGuid().ToString("N"));
    private readonly TestAuthDb authDb = new();

    internal AuthDbContext AuthContext = null!;
    internal UserManager<AppUser> Users = null!;
    internal HouseholdService Households = null!;
    internal string SelfId = null!;
    internal string OtherId = null!;
    internal const string AuthHousehold = "hh-auth";

    private protected virtual LlmOptions ServerLlm => new(); // keyless → the BYOK view

    // Whether the read-only GraphQL API (and its Settings "API access" section) is on. Off by default;
    // the API-access tests flip it.
    private protected virtual bool GraphQlEnabled => false;

    protected override void RegisterAdditionalServices()
    {
        AuthContext = authDb.CreateDbContext();
        Users = new UserManager<AppUser>(
            new UserStore<AppUser>(AuthContext), Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(), [], [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<AppUser>>.Instance);
        Households = new HouseholdService(AuthContext, Users, Options.Create(new AuthOptions()),
            NullLogger<HouseholdService>.Instance);

        AuthContext.Households.Add(new Household { Id = AuthHousehold, Name = "The Currans" });
        AuthContext.SaveChanges();
        var self = new AppUser { UserName = "jordan@test.local", Email = "jordan@test.local", HouseholdId = AuthHousehold };
        var other = new AppUser { UserName = "wife@test.local", Email = "wife@test.local", HouseholdId = AuthHousehold };
        Users.CreateAsync(self).GetAwaiter().GetResult();
        Users.CreateAsync(other).GetAwaiter().GetResult();
        (SelfId, OtherId) = (self.Id, other.Id);

        var auth = this.AddAuthorization();
        auth.SetAuthorized("jordan@test.local");
        auth.SetClaims(
            new Claim(ClaimTypes.NameIdentifier, SelfId),
            new Claim(HouseholdClaimsPrincipalFactory.HouseholdClaim, AuthHousehold));

        var household = new FakeCurrentHousehold("hh-test");
        var storage = new ReceiptStorage(
            new AppPaths(dataDir, Path.Combine(dataDir, "receipts")), household, NullLogger<ReceiptStorage>.Instance);
        var recipeImages = new RecipeImageStorage(
            new AppPaths(dataDir, Path.Combine(dataDir, "receipts")), household, NullLogger<RecipeImageStorage>.Instance);
        var tokens = new ApiTokenService(authDb);
        Services.AddSingleton(Households);
        Services.AddSingleton(tokens);
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["GraphQL:Enabled"] = GraphQlEnabled ? "true" : "false" })
                .Build());
        Services.AddSingleton(new CircuitAiSettings(Options.Create(ServerLlm)));
        Services.AddSingleton(Options.Create(ServerLlm));
        Services.AddSingleton(new AiUsageMeter(Factory, Options.Create(ServerLlm),
            Options.Create(new ElevenLabsOptions()), new FakeEntitlements(), NullLogger<AiUsageMeter>.Instance));
        Services.AddSingleton(new UserDataService(Factory, household, storage, recipeImages, null,
            tokens, NullLogger<UserDataService>.Instance));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Users.Dispose();
        AuthContext.Dispose();
        authDb.Dispose();
        if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
    }

    private protected IRenderedComponent<Settings> RenderSettings()
    {
        var cut = Render<Settings>();
        cut.WaitForState(() => cut.FindAll("section.panel").Count > 0);
        return cut;
    }

    private protected static AngleSharp.Dom.IElement Section(IRenderedComponent<Settings> cut, string heading) =>
        cut.FindAll("section.panel").Single(s => s.QuerySelector("h2")?.TextContent.Contains(heading) == true);
}

/// <summary>The keyless (bring-your-own-key) deployment view — plus everything on the page that
/// doesn't depend on key mode.</summary>
public class SettingsPageTests : SettingsTestBase
{
    // ------------------------------------------------------------------------ guided walkthrough

    [Fact]
    public void The_walkthrough_s_last_step_has_something_to_ring_here()
    {
        // TourScript's final step rings "[data-tour=ai-keys]" — bringing your own key is the one thing
        // a new visitor must do on this page. The ring is best-effort by design, so a selector that
        // stopped matching would fail SILENTLY and forever; this is what notices.
        var cut = RenderSettings();

        var step = ShelfAware.Core.Onboarding.TourScript.Steps[^1];
        Assert.Equal("/settings", step.Route);
        Assert.NotNull(cut.Find(step.Anchor!));
    }

    [Fact]
    public async Task Take_the_tour_restarts_the_walkthrough()
    {
        // A dismissal is remembered, so this button is the only way back in for someone who closed it.
        var started = 0;
        Tour.StartRequested += () => { started++; return Task.CompletedTask; };
        var cut = RenderSettings();

        await cut.InvokeAsync(() =>
            Section(cut, "Guided walkthrough").QuerySelectorAll("button")
                .Single(b => b.TextContent.Trim() == "Take the tour").ClickAsync(new()));

        Assert.Equal(1, started);
    }

    // ------------------------------------------------------------------------ pantry preferences

    [Fact]
    public async Task The_import_mode_radios_read_and_write_the_household_setting()
    {
        var cut = RenderSettings();

        // Smart is the default — checked without any stored setting.
        var radios = cut.FindAll(".import-mode input");
        Assert.True(radios[0].HasAttribute("checked"));

        cut.FindAll(".import-mode input")[1].Change(true); // Review everything
        await cut.WaitForAssertionAsync(async () =>
            Assert.Equal("Review", await AppSettings.GetAsync(SettingKeys.ImportMode)));

        cut.FindAll(".import-mode input")[2].Change(true); // Confirm everything
        await cut.WaitForAssertionAsync(async () =>
            Assert.Equal("Auto", await AppSettings.GetAsync(SettingKeys.ImportMode)));
    }

    [Fact]
    public async Task The_expiration_toggle_writes_through_and_reassures_about_dormant_dates()
    {
        // Dates recorded while the feature was on, now off: the panel must say they're dormant,
        // not deleted — "off is dormant, not destructive" is the toggle's contract.
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Whole Milk", Category = Category.Dairy,
                Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-3), Quantity = 1m, ExpirationDate = Today.AddDays(4) }],
            });
            db.SaveChanges();
        }
        var cut = RenderSettings();
        Assert.Contains("kept but dormant", Section(cut, "Expiration dates").TextContent);

        Section(cut, "Expiration dates").QuerySelector("input[type=checkbox]")!.Change(true);

        await cut.WaitForAssertionAsync(async () =>
        {
            Assert.Equal("true", await AppSettings.GetAsync(SettingKeys.TrackExpirationDates));
            Assert.DoesNotContain("kept but dormant", Section(cut, "Expiration dates").TextContent);
        });
    }

    [Fact]
    public async Task The_recipe_add_preference_persists()
    {
        var cut = RenderSettings();
        Section(cut, "Recipe ingredients").QuerySelector("select")!.Change("Auto");
        await cut.WaitForAssertionAsync(async () =>
            Assert.Equal("Auto", await AppSettings.GetAsync(SettingKeys.RecipeAddConfirm)));
    }

    [Fact]
    public void The_api_access_section_is_hidden_when_the_flag_is_off()
    {
        // GraphQlEnabled is false in this base view — the section (and every token control) is absent.
        var cut = RenderSettings();
        Assert.DoesNotContain(cut.FindAll("section.panel h2"), h => h.TextContent.Contains("API access"));
    }

    // ------------------------------------------------------------------------------------- usage

    [Fact]
    public void Usage_renders_the_recorded_days_and_an_honest_zero_state()
    {
        var empty = RenderSettings();
        Assert.Contains("No AI usage recorded yet", Section(empty, "AI usage").TextContent);

        using (var db = Db.CreateDbContext())
        {
            db.AiUsages.Add(new AiUsage { Day = Today, Calls = 3, InputTokens = 1200, OutputTokens = 800 });
            db.AiUsages.Add(new AiUsage { Day = Today.AddDays(-2), Calls = 1, InputTokens = 400, OutputTokens = 100, VoiceSessionMints = 1 });
            db.SaveChanges();
        }
        var cut = RenderSettings();

        var section = Section(cut, "AI usage");
        // Today's stats sum the row the meter keeps per day…
        Assert.Contains("3", section.QuerySelectorAll(".stat").Single(s => s.TextContent.Contains("AI calls")).TextContent);
        Assert.Contains(2000.ToString("N0"), section.QuerySelectorAll(".stat").Single(s => s.TextContent.Contains("tokens")).TextContent);
        // …and the history table lists both days with in/out split out.
        var rows = section.QuerySelectorAll("tbody tr").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(1200.ToString("N0"), rows.Single(r => r.TextContent.Contains($"{Today:MMM d}")).TextContent);
    }

    // ----------------------------------------------------------------------------- BYOK panel

    [Fact]
    public void Saving_keys_applies_to_this_circuit_without_a_reload()
    {
        JSInterop.SetupModule("/js/ai-settings.js").Setup<bool>("save", _ => true).SetResult(true);
        var cut = RenderSettings();
        cut.WaitForState(() => cut.FindAll(".ai-settings").Count > 0);
        var ai = Services.GetRequiredService<CircuitAiSettings>();
        Assert.False(ai.HasKey);

        cut.Find("input[aria-label='Anthropic API key']").Change("sk-ant-test");
        cut.FindAll(".review-actions button").Single(b => b.TextContent.Trim() == "Save keys").Click();

        // "Works without a reload" is the promise: the scoped circuit settings carry the key the
        // moment Save lands, not on the next visit.
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Saved in this browser.", cut.Markup);
            Assert.True(ai.HasKey);
        });

        cut.FindAll(".review-actions button").Single(b => b.TextContent.Trim() == "Forget my keys").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Cleared — your keys are no longer stored in this browser.", cut.Markup);
            Assert.False(ai.HasKey); // back to the server config, which is keyless here
        });
    }

    [Fact]
    public void Switching_provider_remembers_each_saved_config_separately()
    {
        JSInterop.SetupModule("/js/ai-settings.js").Setup<bool>("save", _ => true).SetResult(true);
        var cut = RenderSettings();
        cut.WaitForState(() => cut.FindAll(".ai-settings").Count > 0);

        // SAVED configs are what survive a switch — Save snapshots the current provider's fields
        // into the per-provider map (typed-but-unsaved text is ordinary form state and does not).
        cut.Find("input[aria-label='Anthropic API key']").Change("sk-ant-test");
        cut.FindAll(".review-actions button").Single(b => b.TextContent.Trim() == "Save keys").Click();
        cut.WaitForState(() => cut.Markup.Contains("Saved in this browser."));

        cut.Find("select[aria-label='AI provider']").Change("OpenAI");

        // A fresh provider starts with ITS defaults — not the other provider's key on screen.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("", cut.Find("input[aria-label='OpenAI API key']").GetAttribute("value") ?? "");
            Assert.Equal("gpt-4o-mini", cut.Find("input[aria-label='Extraction model']").GetAttribute("value"));
        });

        cut.Find("select[aria-label='AI provider']").Change("Anthropic");
        cut.WaitForAssertion(() =>
            Assert.Equal("sk-ant-test", cut.Find("input[aria-label='Anthropic API key']").GetAttribute("value")));
    }

    [Fact]
    public void A_browser_that_cannot_listen_fails_the_calibration_kindly()
    {
        // Loose JS answers isSupported with false — the wizard must refuse with a sentence, not
        // open a dead microphone session.
        var cut = RenderSettings();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Calibrate listening")).Click();
        cut.WaitForAssertion(() =>
            Assert.Equal("This browser can't listen.", cut.Find(".error[role=status]").TextContent.Trim()));
        Assert.Contains("Not calibrated — using defaults.", Section(cut, "Hands-free listening").TextContent);
    }

    // ---------------------------------------------------------------------------- the household

    [Fact]
    public async Task The_invite_lifecycle_reads_no_code_until_asked_and_single_use_by_default()
    {
        var cut = RenderSettings();
        cut.WaitForState(() => Section(cut, "Household").TextContent.Contains("Invite code"));

        // No code exists until someone asks — there's never a spare key lying around.
        Assert.Equal("—", cut.Find(".invite-code").TextContent.Trim());
        Assert.Contains("nobody can join this household until you generate one",
            Section(cut, "Household").TextContent);

        cut.FindAll("button").Single(b => b.TextContent.Contains("Generate invite code")).Click();

        string code = "";
        cut.WaitForAssertion(() =>
        {
            code = cut.Find(".invite-code").TextContent.Trim();
            Assert.NotEqual("—", code);
            // The checkbox defaults to single use, and the status sentence says exactly that.
            Assert.Contains("has one use left", Section(cut, "Household").TextContent);
        });
        Assert.Equal(code, (await AuthContext.Households.AsNoTracking().SingleAsync(h => h.Id == AuthHousehold)).InviteCode);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Copy").Click();
        var copied = JSInterop.Invocations.Single(i => i.Identifier == "navigator.clipboard.writeText");
        Assert.Equal(code, (string)copied.Arguments[0]!);

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Replace").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("the old one no longer works", cut.Markup);
            Assert.NotEqual(code, cut.Find(".invite-code").TextContent.Trim());
        });

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Clear code").Click();
        cut.WaitForAssertion(() => Assert.Equal("—", cut.Find(".invite-code").TextContent.Trim()));
        Assert.Null((await AuthContext.Households.AsNoTracking().SingleAsync(h => h.Id == AuthHousehold)).InviteCode);
    }

    [Fact]
    public void A_refused_clipboard_on_the_invite_copy_says_so_instead_of_tearing_down()
    {
        // The code stays visible on screen, so the honest fallback is the manual one.
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true)
            .SetException(new JSException("Write permission denied."));
        var cut = RenderSettings();
        cut.WaitForState(() => Section(cut, "Household").TextContent.Contains("Invite code"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Generate invite code")).Click();
        cut.WaitForState(() => cut.Find(".invite-code").TextContent.Trim() != "—");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Copy").Click();

        cut.WaitForAssertion(() => Assert.Contains("Couldn't copy — select the code and copy it by hand.",
            Section(cut, "Household").TextContent));
    }

    [Fact]
    public async Task Removing_a_member_asks_first_and_never_offers_removal_of_yourself()
    {
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        var cut = RenderSettings();
        cut.WaitForState(() => cut.FindAll(".member-list li").Count == 2);

        // Your own row says "you" and carries no Remove — the last-member/self rules live in the
        // service; the page's job is not to offer the act at all.
        var selfRow = cut.FindAll(".member-list li").Single(l => l.TextContent.Contains("jordan@"));
        Assert.Contains("you", selfRow.TextContent);
        Assert.Null(selfRow.QuerySelector("button"));

        cut.Find(".member-list button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("wife@test.local is no longer in this household.", cut.Markup);
            Assert.Single(cut.FindAll(".member-list li"));
        });
        // Removal really detached the account (and the service bumped its stamp — service-pinned).
        Assert.Null((await Users.FindByIdAsync(OtherId))!.HouseholdId);
    }

    [Fact]
    public async Task Renaming_the_household_persists_and_the_button_arms_only_on_change()
    {
        var cut = RenderSettings();
        cut.WaitForState(() => Section(cut, "Household").TextContent.Contains("Household name"));

        var rename = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Rename");
        Assert.True(rename.HasAttribute("disabled")); // unchanged name = nothing to save

        cut.Find("input[aria-label='Household name']").Input("The Curran Pantry");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Rename").Click();

        cut.WaitForAssertion(() => Assert.Contains("Household renamed.", cut.Markup));
        Assert.Equal("The Curran Pantry",
            (await AuthContext.Households.AsNoTracking().SingleAsync(h => h.Id == AuthHousehold)).Name);
    }

    // ------------------------------------------------------------------------------ delete data

    [Fact]
    public async Task Delete_everything_arms_only_on_the_exact_word_and_then_really_deletes()
    {
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product { Name = "Doomed Beans", Category = Category.Pantry });
            db.SaveChanges();
        }
        var cut = RenderSettings();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Delete all my data")).Click();
        cut.WaitForState(() => cut.FindAll(".danger-confirm").Count == 1);

        var destroy = cut.FindAll(".danger-confirm button").Single(b => b.TextContent.Contains("Permanently delete"));
        Assert.True(destroy.HasAttribute("disabled"));

        // Close but not exact must not arm it — the word is the signature, case and all.
        cut.Find(".danger-confirm input[type=text]").Input("delete");
        Assert.True(cut.FindAll(".danger-confirm button")
            .Single(b => b.TextContent.Contains("Permanently delete")).HasAttribute("disabled"));

        cut.Find(".danger-confirm input[type=text]").Input("DELETE");
        cut.WaitForAssertion(() => Assert.False(cut.FindAll(".danger-confirm button")
            .Single(b => b.TextContent.Contains("Permanently delete")).HasAttribute("disabled")));

        cut.FindAll(".danger-confirm button").Single(b => b.TextContent.Contains("Permanently delete")).Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("your settings are back to their defaults", cut.Markup));
        await using var raw = Db.CreateUnscopedContext();
        Assert.Empty(await raw.Products.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public void The_delete_confirmation_counts_what_it_is_about_to_remove()
    {
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product { Name = "Doomed Beans", Category = Category.Pantry });
            db.Products.Add(new Product { Name = "Doomed Rice", Category = Category.Pantry });
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.ImportMode, Value = "Auto" });
            db.SaveChanges();
        }
        var cut = RenderSettings();

        cut.FindAll("button").Single(b => b.TextContent.Contains("Delete all my data")).Click();

        // Three: the two products AND the setting. The settings row is in the count because the delete
        // takes it — a warning that under-reported by the rows nobody thinks of would be the wrong kind
        // of reassuring. Formatted rather than hardcoded: "1,234" is only correct in some cultures.
        cut.WaitForAssertion(() =>
            Assert.Contains($"{3:N0} records will be removed", cut.Find(".danger-confirm").TextContent));
    }

    [Fact]
    public async Task Delete_everything_puts_the_settings_controls_back_to_their_defaults()
    {
        // The wipe takes the settings rows along with the pantry, and this page read them once on init.
        // Without a re-read, the controls keep offering choices the database no longer holds — a screen
        // stating something the data doesn't, one scroll above the button that just said otherwise.
        using (var db = Db.CreateDbContext())
        {
            db.Products.Add(new Product { Name = "Doomed Beans", Category = Category.Pantry });
            db.SaveChanges();
        }
        await AppSettings.SetAsync(SettingKeys.ImportMode, "Auto");
        await AppSettings.SetAsync(SettingKeys.RecipeAddConfirm, "Auto");
        await AppSettings.SetAsync(SettingKeys.TrackExpirationDates, "true");

        var cut = RenderSettings();
        Assert.True(cut.FindAll(".import-mode input")[2].HasAttribute("checked"));
        Assert.True(Section(cut, "Expiration dates").QuerySelector("input[type=checkbox]")!.HasAttribute("checked"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Delete all my data")).Click();
        cut.WaitForState(() => cut.FindAll(".danger-confirm").Count == 1);
        cut.Find(".danger-confirm input[type=text]").Input("DELETE");
        cut.WaitForAssertion(() => Assert.False(cut.FindAll(".danger-confirm button")
            .Single(b => b.TextContent.Contains("Permanently delete")).HasAttribute("disabled")));
        cut.FindAll(".danger-confirm button").Single(b => b.TextContent.Contains("Permanently delete")).Click();

        cut.WaitForAssertion(() =>
        {
            // Smart, Confirm, and tracking off — the same answers the store gives with no rows at all.
            Assert.True(cut.FindAll(".import-mode input")[0].HasAttribute("checked"));
            Assert.False(Section(cut, "Expiration dates").QuerySelector("input[type=checkbox]")!.HasAttribute("checked"));
            Assert.Equal("Confirm", ((AngleSharp.Html.Dom.IHtmlSelectElement)
                Section(cut, "Recipe ingredients").QuerySelector("select")!).Value);
        });
        await using var check = Db.CreateDbContext();
        Assert.Empty(await check.AppSettings.ToListAsync());
    }
}

/// <summary>The managed-key deployment: the host's config is authoritative and the key panel is
/// hidden — there must be nothing on this page inviting a visitor to paste a key that would never
/// be used.</summary>
public class SettingsManagedModeTests : SettingsTestBase
{
    private protected override LlmOptions ServerLlm => new() { KeyMode = "managed", ApiKey = "sk-host-key" };

    [Fact]
    public void A_managed_deployment_hides_the_key_panel_and_shows_usage_instead()
    {
        var cut = RenderSettings();

        var section = cut.FindAll("section.panel")
            .Single(s => s.QuerySelector("h2")?.TextContent.Contains("AI provider") == true);
        Assert.Contains("runs on the host's own keys", section.TextContent);
        Assert.Empty(cut.FindAll(".ai-settings"));
        Assert.Empty(cut.FindAll("input[aria-label='Anthropic API key']"));
    }
}

/// <summary>The read-only GraphQL API is enabled, so the Settings "API access" section is present and its
/// mint/list/revoke flow works.</summary>
public class SettingsApiAccessTests : SettingsTestBase
{
    private protected override bool GraphQlEnabled => true;

    private static void ClickButton(IRenderedComponent<Settings> cut, string section, string text) =>
        Section(cut, section).QuerySelectorAll("button").Single(b => b.TextContent.Trim() == text).Click();

    [Fact]
    public void The_section_appears_when_the_flag_is_on()
    {
        var cut = RenderSettings();
        Assert.Contains(cut.FindAll("section.panel h2"), h => h.TextContent.Contains("API access"));
    }

    [Fact]
    public void Creating_a_token_shows_the_secret_once_lists_it_then_revoke_marks_it_revoked()
    {
        var cut = RenderSettings();

        Section(cut, "API access").QuerySelector("input[aria-label='Token name']")!.Input("meal planner");
        ClickButton(cut, "API access", "Create token");

        // The secret is shown once, and the token is listed as Live under its name.
        cut.WaitForAssertion(() =>
        {
            var section = Section(cut, "API access");
            Assert.Contains("Copy your new token now", section.TextContent);
            Assert.Contains("meal planner", section.TextContent);
            Assert.Contains("Live", section.TextContent);
            Assert.StartsWith("sa_", section.QuerySelector("code.invite-code")!.TextContent);
        });

        // Revoke it — the row flips to Revoked and the Revoke button is gone.
        ClickButton(cut, "API access", "Revoke");
        cut.WaitForAssertion(() =>
        {
            var section = Section(cut, "API access");
            Assert.Contains("Revoked", section.TextContent);
            Assert.DoesNotContain(section.QuerySelectorAll("button"), b => b.TextContent.Trim() == "Revoke");
        });
    }
}
