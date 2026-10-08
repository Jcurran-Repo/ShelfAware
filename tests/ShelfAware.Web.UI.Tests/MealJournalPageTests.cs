using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Journal;
using ShelfAware.Web.Components.Pages;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The /journal page: a month of calories per day, per week and for the month, a day's meals underneath,
/// and the hand-edits that make Reginald's estimates correctable. Real SQLite under the real per-member
/// scope, so "another member's meals never appear" is tested against the filter that enforces it.
/// </summary>
public class MealJournalPageTests : PageTestContext
{
    private IRenderedComponent<MealJournalPage> RenderPage()
    {
        var cut = Render<MealJournalPage>();
        cut.WaitForState(() => cut.FindAll(".journalcal").Count > 0);
        return cut;
    }

    private void Seed(string food, int? calories, bool estimated = false, MealSlot slot = MealSlot.Lunch,
        DateOnly? on = null, string? member = null)
    {
        var me = Db.MemberId;
        if (member is not null) Db.MemberId = member;
        using (var db = Db.CreateDbContext())
        {
            db.JournalEntries.Add(new JournalEntry
            {
                Food = food, Calories = calories, CaloriesEstimated = estimated, Slot = slot,
                EatenOn = on ?? Today, LoggedAt = DateTimeOffset.Now,
            });
            db.SaveChanges();
        }
        Db.MemberId = me;
    }

    private List<JournalEntry> Stored()
    {
        using var db = Db.CreateDbContext();
        return [.. db.JournalEntries.AsNoTracking().OrderBy(e => e.Id)];
    }

    private static AngleSharp.Dom.IElement DayCell(IRenderedComponent<MealJournalPage> cut, DateOnly day) =>
        cut.FindAll(".journalcal-day").Single(b =>
            b.GetAttribute("aria-label")!.StartsWith(MealJournal.Label(JournalPeriod.Day, new DateSpan(day, day))));

    [Fact]
    public void The_month_shows_its_total_and_each_day_and_week_its_own()
    {
        Seed("Eggs", 200, estimated: true, slot: MealSlot.Breakfast);
        Seed("Salad", 350);

        var cut = RenderPage();

        Assert.Contains("~550 kcal this month", Collapsed(cut.Find(".journal-monthtotal")));
        Assert.Equal("~550 kcal", Collapsed(DayCell(cut, Today).QuerySelector(".journalcal-kcal")!));
        var week = MealJournal.Label(JournalPeriod.Week, MealJournal.SpanOf(JournalPeriod.Week, Today));
        var weekCell = cut.FindAll(".journalcal-week").Single(c => c.GetAttribute("aria-label")!.Contains(week));
        Assert.Equal("~550 kcal", Collapsed(weekCell));
    }

    [Fact]
    public void Another_members_meals_never_appear()
    {
        Seed("Their cake", 900, member: "member-spouse");

        var cut = RenderPage();

        Assert.DoesNotContain("Their cake", cut.Markup);
        Assert.Contains("Nothing with a calorie count logged this month yet.", cut.Find(".journal-monthtotal").TextContent);
    }

    [Fact]
    public void Today_opens_with_its_meals_in_order_and_flags_what_is_estimated_or_uncounted()
    {
        Seed("Sandwich", 450, slot: MealSlot.Lunch);
        Seed("Eggs", 200, estimated: true, slot: MealSlot.Breakfast);
        Seed("Mystery stew", null, slot: MealSlot.Dinner);

        var cut = RenderPage();

        Assert.Equal(["Breakfast ~200 kcal", "Lunch 450 kcal", "Dinner"],
            cut.FindAll(".journal-meal h4").Select(h => Collapsed(h)));
        Assert.Contains("1 item has no calorie count, so it isn't in the day's total.", cut.Find(".journal-day").TextContent);
        var eggs = cut.FindAll(".journal-item").Single(i => i.TextContent.Contains("Eggs"));
        Assert.NotNull(eggs.QuerySelector(".journal-est-tag"));
        var sandwich = cut.FindAll(".journal-item").Single(i => i.TextContent.Contains("Sandwich"));
        Assert.Null(sandwich.QuerySelector(".journal-est-tag"));
    }

    [Fact]
    public void A_food_added_by_hand_is_saved_with_the_persons_own_number()
    {
        var cut = RenderPage();

        cut.Find(".journal-add input[aria-label='Food to add']").Change("Apple");
        cut.Find(".journal-add select").Change(nameof(MealSlot.Snack));
        cut.Find(".journal-add input[type=number]").Change("95");
        cut.Find(".journal-add").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Added Apple to snack.", cut.Find(".journal-day [role=status]").TextContent));
        var entry = Assert.Single(Stored());
        Assert.Equal(("Apple", MealSlot.Snack, 95, false, Today), (entry.Food, entry.Slot, entry.Calories, entry.CaloriesEstimated, entry.EatenOn));
    }

    [Fact]
    public void A_refused_add_says_why_and_saves_nothing()
    {
        var cut = RenderPage();

        cut.Find(".journal-add input[type=number]").Change("-20");
        cut.Find(".journal-add input[aria-label='Food to add']").Change("Celery");
        cut.Find(".journal-add").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Calories can't be negative.", cut.Find(".journal-day [role=status] .error").TextContent));
        Assert.Empty(Stored());
    }

    [Fact]
    public void Correcting_an_estimate_makes_it_the_persons_number()
    {
        Seed("Burrito", 900, estimated: true);
        var cut = RenderPage();

        cut.Find(".journal-item button[aria-label='Edit Burrito']").Click();
        cut.Find(".journal-item input[type=number]").Change("750");
        cut.Find(".journal-item form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Updated Burrito.", cut.Find(".journal-day [role=status]").TextContent));
        var entry = Assert.Single(Stored());
        Assert.Equal((750, false), (entry.Calories, entry.CaloriesEstimated));
    }

    [Fact]
    public void Renaming_a_food_leaves_its_untouched_estimate_an_estimate()
    {
        Seed("Burrito", 900, estimated: true);
        var cut = RenderPage();

        cut.Find(".journal-item button[aria-label='Edit Burrito']").Click();
        cut.Find(".journal-item input[aria-label='Food']").Change("Bean burrito");
        cut.Find(".journal-item form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Updated Bean burrito.", cut.Find(".journal-day [role=status]").TextContent));
        var entry = Assert.Single(Stored());
        Assert.Equal(("Bean burrito", 900, true), (entry.Food, entry.Calories, entry.CaloriesEstimated));
    }

    [Fact]
    public void Removing_a_food_can_be_undone_exactly_as_it_was()
    {
        Seed("Eggs", 200, estimated: true, slot: MealSlot.Breakfast);
        var cut = RenderPage();

        cut.Find(".journal-item button[aria-label='Remove Eggs']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Removed Eggs.", cut.Find(".journal-day [role=status]").TextContent));
        Assert.Empty(Stored());

        cut.FindAll(".journal-day [role=status] button").Single(b => b.TextContent.Contains("Undo")).Click();

        cut.WaitForAssertion(() => Assert.Contains("Put Eggs back.", cut.Find(".journal-day [role=status]").TextContent));
        var entry = Assert.Single(Stored());
        Assert.Equal(("Eggs", MealSlot.Breakfast, 200, true, Today), (entry.Food, entry.Slot, entry.Calories, entry.CaloriesEstimated, entry.EatenOn));
        Assert.Empty(cut.FindAll(".journal-day [role=status] button")); // one undo per removal
    }

    [Fact]
    public void Days_that_have_not_happened_cannot_be_opened_and_the_month_cannot_run_ahead()
    {
        var cut = RenderPage();

        Assert.All(cut.FindAll(".journalcal-day").Where(b =>
                b.GetAttribute("aria-label")!.EndsWith(": not yet")),
            b => Assert.True(b.HasAttribute("disabled")));
        Assert.True(cut.Find("button[aria-label='Next month']").HasAttribute("disabled"));
    }

    [Fact]
    public void Last_month_is_one_tap_away_and_this_month_one_tap_back()
    {
        var lastMonth = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1);
        Seed("Thanksgiving-sized dinner", 1800, on: lastMonth.AddDays(9));
        var cut = RenderPage();

        cut.Find("button[aria-label='Previous month']").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            MealJournal.Label(JournalPeriod.Month, MealJournal.SpanOf(JournalPeriod.Month, lastMonth)),
            cut.Find(".journal-monthhead h2").TextContent));
        Assert.Contains("1,800 kcal this month", Collapsed(cut.Find(".journal-monthtotal")));
        Assert.False(cut.Find("button[aria-label='Next month']").HasAttribute("disabled"));

        cut.FindAll(".journal-monthhead button").Single(b => b.TextContent == "This month").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Find("button[aria-label='Next month']").HasAttribute("disabled")));
    }

    [Fact]
    public void Opening_another_day_shows_that_days_meals()
    {
        var earlier = Today.Day > 1 ? Today.AddDays(-1) : Today; // stay inside this month's grid either way
        Seed("Leftover pizza", 600, on: earlier, slot: MealSlot.Dinner);
        var cut = RenderPage();

        DayCell(cut, earlier).Click();

        cut.WaitForAssertion(() => Assert.Contains("Leftover pizza", cut.Find(".journal-day").TextContent));
        Assert.Equal("true", DayCell(cut, earlier).GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Telling_reginald_goes_through_the_same_chat_and_a_blank_box_gets_the_journals_own_hint()
    {
        var cut = RenderPage();

        cut.Find("section.quick-update input").Input(" ");
        cut.Find("section.quick-update form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("Say what you ate first", cut.Find("section.quick-update [role=status]").TextContent));
        Assert.Empty(Chat.Asked);

        cut.Find("section.quick-update input").Input("a turkey sandwich for lunch");
        cut.Find("section.quick-update form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(["a turkey sandwich for lunch"], Chat.Asked));
    }

    [Fact]
    public async Task A_meal_logged_by_the_roaming_voice_agent_appears_without_a_reload()
    {
        var cut = RenderPage();
        Seed("Banana", 105, estimated: true, slot: MealSlot.Snack);

        await cut.InvokeAsync(() => Coordinator.NotifyPantryChangedAsync());

        cut.WaitForAssertion(() => Assert.Contains("Banana", cut.Find(".journal-day").TextContent));
    }
}
