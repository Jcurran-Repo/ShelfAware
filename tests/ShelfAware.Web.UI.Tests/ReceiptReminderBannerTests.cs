using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Llm;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Components;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The dashboard's receipt reminder: it speaks the household's OWN learned gap, and dismissing it is a
/// snooze that is actually written down. Rendered over the real service and the real settings table —
/// the decision itself is pinned in ReceiptReminderServiceTests and UploadCadenceTests; what these hold
/// is that the banner shows the engine's numbers and that its two buttons do what they say.
/// <para>Deliberately no bare "and it stays away when…" test here. A banner that renders nothing is
/// indistinguishable from a banner that has not finished loading, so such a test passes whether the rule
/// works or not — green would be exactly what the defect produces. Every stand-down case the cadence owns
/// is pinned where it can actually fail: over the service and over the cadence itself. The ONE stand-down
/// this component owns — no AI key, so the upload it points at cannot run — is pinned below by making the
/// same instance show the banner once a key arrives, which an unfinished load could never do.</para>
/// </summary>
public class ReceiptReminderBannerTests : PageTestContext
{
    protected override void RegisterAdditionalServices()
    {
        // These tests render the banner ITSELF — clear the page-harness stubs (the dashboard stubs it).
        ComponentFactories.Clear();
        Services.AddSingleton(new ReceiptReminderService(Factory, AppSettings));
    }

    /// <summary>A household that has uploaded weekly for a month and then gone quiet for
    /// <paramref name="quietDays"/> — the last upload is that many days before today.</summary>
    private async Task WeeklyUploaderQuietFor(int quietDays)
    {
        await using var db = Db.CreateDbContext();
        for (var weeksAgo = 0; weeksAgo < 4; weeksAgo++)
        {
            var day = Today.AddDays(-quietDays - (weeksAgo * 7));
            db.Receipts.Add(new Receipt
            {
                ImagePath = $"receipt-{weeksAgo}",
                Status = ReceiptStatus.Confirmed,
                PurchasedAt = day,
                UploadedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(15, 0))),
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task It_names_the_households_own_gap_not_a_fixed_week()
    {
        await WeeklyUploaderQuietFor(9);

        var cut = Render<ReceiptReminderBanner>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".reminder-banner")));
        var text = Collapsed(cut.Find(".reminder-banner"));
        Assert.Contains("9 days", text);                      // the quiet stretch
        Assert.Contains("about every 7 days", text);          // …measured against their own rhythm
        // The day it names is the day they UPLOADED, so it says so: /receipts dates the same row by its
        // printed PurchasedAt, and "your last receipt, on the 13th" beside a different date there is two
        // screens disagreeing about one receipt.
        Assert.Contains("since you last uploaded a receipt", text);
    }

    [Fact]
    public async Task Not_now_hides_it_and_writes_the_snooze_down()
    {
        await WeeklyUploaderQuietFor(9);
        var cut = Render<ReceiptReminderBanner>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".reminder-banner")));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Not now").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".reminder-banner")));
        // Persisted, not merely hidden for this render: a dismissal the next page load undoes is not a
        // dismissal. One more of the household's own gaps — seven days from today.
        await cut.WaitForAssertionAsync(async () => Assert.Equal(
            Today.AddDays(7).ToString("yyyy-MM-dd"),
            await AppSettings.GetAsync(SettingKeys.ReceiptReminderSnoozedUntil)));
    }

    [Fact]
    public async Task The_corner_x_snoozes_it_the_same_way_as_the_button()
    {
        // Two affordances, one meaning — a × that only hid it for this render while "Not now" wrote a
        // snooze would be two answers to the same question.
        await WeeklyUploaderQuietFor(9);
        var cut = Render<ReceiptReminderBanner>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".reminder-banner")));

        cut.Find(".banner-x").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".reminder-banner")));
        await cut.WaitForAssertionAsync(async () => Assert.Equal(
            Today.AddDays(7).ToString("yyyy-MM-dd"),
            await AppSettings.GetAsync(SettingKeys.ReceiptReminderSnoozedUntil)));
    }

    [Fact]
    public async Task Upload_a_receipt_goes_to_the_upload_page()
    {
        await WeeklyUploaderQuietFor(9);
        var cut = Render<ReceiptReminderBanner>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".reminder-banner")));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Upload a receipt").Click();

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith("/receipt", nav.Uri));
    }

    [Fact]
    public async Task A_day_of_quiet_is_a_day_not_days()
    {
        // A household that uploads most days: the floor holds the reminder to three quiet days, and the
        // gap it reports is one day — written "1 day", which is the wording this repo has got wrong before.
        await using (var db = Db.CreateDbContext())
        {
            for (var ago = 3; ago <= 6; ago++)
            {
                var day = Today.AddDays(-ago);
                db.Receipts.Add(new Receipt
                {
                    ImagePath = $"daily-{ago}",
                    Status = ReceiptStatus.Confirmed,
                    UploadedAt = new DateTimeOffset(day.ToDateTime(new TimeOnly(15, 0))),
                });
            }
            await db.SaveChangesAsync();
        }

        var cut = Render<ReceiptReminderBanner>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".reminder-banner")));
        var text = Collapsed(cut.Find(".reminder-banner"));
        Assert.Contains("3 days since you last uploaded a receipt", text);
        // A frequency, not a count: "about every 1 day" is not a sentence anyone says, and the household
        // it would be said to is the one that sees this banner most often.
        Assert.Contains("about every day.", text);
        Assert.DoesNotContain("1 days", text);
        Assert.DoesNotContain("every 1 day", text);
    }

    [Fact]
    public async Task Dismissing_hands_focus_back_to_the_page_after_the_snooze_is_written()
    {
        // The banner takes a whole region out of the DOM, and focus inside a removed element falls to the
        // body — the person who just pressed × would Tab from the top of the page again. Where focus
        // lands is the page's business, so the banner only says "I'm gone"; Home sends it to its heading.
        await WeeklyUploaderQuietFor(9);
        var dismissals = 0;
        string? snoozeWhenTold = null;
        var cut = Render<ReceiptReminderBanner>(p => p.Add(
            b => b.OnDismissed,
            async () =>
            {
                dismissals++;
                // Raised AFTER the write, so a failure out in the page can never cost the household the
                // dismissal they just made — and the page is focusing a DOM the region has already left.
                snoozeWhenTold = await AppSettings.GetAsync(SettingKeys.ReceiptReminderSnoozedUntil);
            }));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".reminder-banner")));

        cut.Find(".banner-x").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, dismissals));
        Assert.Equal(Today.AddDays(7).ToString("yyyy-MM-dd"), snoozeWhenTold);
    }

    [Fact]
    public async Task With_no_AI_key_it_stands_down_and_comes_back_when_one_arrives()
    {
        // Uploading a receipt needs an extraction the circuit can't make without a key, so nudging toward
        // it is a nudge into a wall — and on a keyless browser the first-run banner is already on screen
        // saying the useful thing, which is how the two came to be stacked.
        //
        // Driven key-first ON PURPOSE. The stand-down direction alone would be a test of an empty render,
        // which an unfinished load produces just as well; starting from a rendered banner proves the load
        // is done, so the disappearance can only be the gate. The banner re-reads the key on every render
        // (it is not latched), so this pins the production order too — a BYOK visitor's key lands after
        // the first render, and the banner appears then rather than never.
        await WeeklyUploaderQuietFor(9);
        var ai = Services.GetRequiredService<CircuitAiSettings>();

        var cut = Render<ReceiptReminderBanner>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".reminder-banner")));

        ai.Apply(AiProvider.Anthropic, apiKey: "", extractionModel: null, chatModel: null);
        cut.Render();
        Assert.Empty(cut.FindAll(".reminder-banner"));

        ai.Apply(AiProvider.Anthropic, apiKey: "sk-a-key-at-last", extractionModel: null, chatModel: null);
        cut.Render();
        Assert.NotEmpty(cut.FindAll(".reminder-banner"));
    }
}
