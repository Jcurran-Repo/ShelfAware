using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Journal;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// Reginald's two journal tools: <c>log_meal</c> ("I had two eggs for breakfast") and <c>query_journal</c>
/// ("how many calories this week?"). The model is scripted; what is pinned is what the handlers write, what
/// they hand back for the model to say, and what the household is charged for it.
/// </summary>
public class MealJournalChatTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>An in-memory journal that applies the real write rule, as the real service does.</summary>
    private sealed class FakeMealJournal : IMealJournal
    {
        public List<JournalEntry> Entries { get; } = [];
        public List<JournalDraft> Drafts { get; } = [];

        public Task<JournalWrite> LogAsync(JournalDraft draft, CancellationToken cancellationToken = default)
        {
            Drafts.Add(draft);
            if (MealJournal.Problem(draft.Food, draft.Calories, draft.EatenOn, Today) is { } problem)
                return Task.FromResult(JournalWrite.Refused(problem));
            var entry = new JournalEntry
            {
                Id = Entries.Count + 1, Food = draft.Food.Trim(), Slot = draft.Slot, EatenOn = draft.EatenOn,
                Calories = draft.Calories, CaloriesEstimated = draft.Calories is not null && draft.CaloriesEstimated,
                LoggedAt = DateTimeOffset.Now,
            };
            Entries.Add(entry);
            return Task.FromResult(JournalWrite.Saved(entry));
        }

        public Task<IReadOnlyList<JournalEntry>> GetAsync(DateSpan span, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<JournalEntry>>([.. Entries.Where(e => span.Contains(e.EatenOn))]);
    }

    private static AnthropicPantryChat Chat(IChatClient client, FakeMealJournal? journal, FakePantryStore? store = null) =>
        new(client, Options.Create(new LlmOptions()), store ?? new FakePantryStore(), NullLogger<AnthropicPantryChat>.Instance,
            journal: journal);

    /// <summary>What the tool told the model, read off the conversation the model saw next.</summary>
    private static string ToolReply(FakeChatClient client) =>
        string.Join(" | ", client.ReceivedMessages[^1]
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result?.ToString() ?? ""));

    private static FakeChatClient Script(params FunctionCallContent[] calls) =>
        new(() => Responses.ToolCalls(calls), () => Responses.Text("Logged."));

    // --- log_meal -------------------------------------------------------------------------------

    [Fact]
    public async Task A_meal_is_logged_with_reginalds_estimate_marked_as_one()
    {
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal", ("food", "2 scrambled eggs"), ("meal", "Breakfast"), ("calories", 180)));

        var result = await Chat(client, journal).HandleAsync("I had two scrambled eggs for breakfast");

        Assert.True(result.Success);
        Assert.Equal(new JournalDraft("2 scrambled eggs", MealSlot.Breakfast, Today, 180, CaloriesEstimated: true), Assert.Single(journal.Drafts));
        Assert.Contains("journal → 2 scrambled eggs", result.Actions);
        Assert.Contains("~180 kcal", ToolReply(client));
        Assert.Contains("an estimate", ToolReply(client));
    }

    [Fact]
    public async Task A_number_the_user_said_is_theirs_not_an_estimate()
    {
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal",
            ("food", "protein bar"), ("meal", "snack"), ("calories", 250), ("calories_stated", true)));

        await Chat(client, journal).HandleAsync("a 250 calorie protein bar");

        Assert.False(Assert.Single(journal.Drafts).CaloriesEstimated);
        Assert.Contains("250 kcal.", ToolReply(client));
        Assert.DoesNotContain("estimate", ToolReply(client));
    }

    [Fact]
    public async Task A_stated_date_is_used_and_today_is_the_default()
    {
        var journal = new FakeMealJournal();
        var yesterday = Today.AddDays(-1);
        var client = Script(
            Responses.Call("log_meal", ("food", "pizza"), ("meal", "Dinner"), ("calories", 600), ("date", yesterday.ToString("yyyy-MM-dd"))),
            Responses.Call("log_meal", ("food", "apple"), ("meal", "Snack"), ("calories", 95)));

        await Chat(client, journal).HandleAsync("pizza last night and an apple just now");

        Assert.Equal([yesterday, Today], journal.Drafts.Select(d => d.EatenOn));
    }

    [Fact]
    public async Task An_unreadable_date_is_refused_rather_than_logged_as_today()
    {
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal", ("food", "pizza"), ("meal", "Dinner"), ("date", "last Tuesday")));

        await Chat(client, journal).HandleAsync("pizza last Tuesday");

        Assert.Empty(journal.Drafts);
        Assert.Contains("pass it as YYYY-MM-DD", ToolReply(client));
    }

    [Theory]
    [InlineData("Brunch")]
    [InlineData("7")]
    [InlineData(null)]
    public async Task A_meal_that_is_not_one_of_the_four_is_refused(string? meal)
    {
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal", ("food", "pancakes"), ("meal", meal)));

        await Chat(client, journal).HandleAsync("pancakes for brunch");

        Assert.Empty(journal.Drafts);
        Assert.Contains("meal must be one of", ToolReply(client));
    }

    [Fact]
    public async Task A_blank_food_is_refused_before_anything_is_written()
    {
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal", ("food", "  "), ("meal", "Lunch")));

        await Chat(client, journal).HandleAsync("I ate");

        Assert.Empty(journal.Drafts);
        Assert.Contains("Say what was eaten", ToolReply(client));
    }

    [Fact]
    public async Task The_write_rules_refusal_reaches_the_model_and_nothing_is_written()
    {
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal",
            ("food", "cake"), ("meal", "Dinner"), ("calories", 300), ("date", Today.AddDays(2).ToString("yyyy-MM-dd"))));

        var result = await Chat(client, journal).HandleAsync("I'll have cake on Saturday");

        Assert.Empty(journal.Entries);
        Assert.Empty(result.Actions);
        Assert.Contains("hasn't happened yet", ToolReply(client));
    }

    [Fact]
    public async Task A_saved_recipe_is_logged_at_its_own_calorie_figure_times_servings()
    {
        // The journal and the Reports tab's calories-cooked chart must price the same dish the same way —
        // so the model's fresh guess (900) loses to the recipe's own per-serving estimate.
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Chicken Chili", HasSteps: true, CaloriesPerServing: 420));
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal",
            ("food", "chili"), ("meal", "Dinner"), ("calories", 900), ("calories_stated", true),
            ("recipe_name", "chili"), ("servings", 1.5)));

        await Chat(client, journal, store).HandleAsync("I had a bowl and a half of the chicken chili");

        var draft = Assert.Single(journal.Drafts);
        Assert.Equal(630, draft.Calories);
        Assert.True(draft.CaloriesEstimated); // a recipe's figure is an estimate, whatever the model claimed
        Assert.Contains("from the saved Chicken Chili recipe", ToolReply(client));
    }

    [Fact]
    public async Task A_recipe_with_no_figure_or_no_match_keeps_the_models_estimate()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Chicken Chili", HasSteps: true, CaloriesPerServing: null));
        var journal = new FakeMealJournal();
        var client = Script(
            Responses.Call("log_meal", ("food", "chili"), ("meal", "Dinner"), ("calories", 500), ("recipe_name", "chicken chili")),
            Responses.Call("log_meal", ("food", "tacos"), ("meal", "Dinner"), ("calories", 450), ("recipe_name", "fish tacos")));

        await Chat(client, journal, store).HandleAsync("chili and tacos");

        Assert.Equal([500, 450], journal.Drafts.Select(d => d.Calories));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public async Task Implausible_servings_of_a_recipe_are_refused(double servings)
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Chicken Chili", HasSteps: true, CaloriesPerServing: 420));
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal",
            ("food", "chili"), ("meal", "Dinner"), ("recipe_name", "Chicken Chili"), ("servings", servings)));

        await Chat(client, journal, store).HandleAsync("chili");

        Assert.Empty(journal.Drafts);
        Assert.Contains("servings must be", ToolReply(client));
    }

    [Fact]
    public async Task Twenty_servings_is_the_most_and_is_allowed()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Party Mix", HasSteps: false, CaloriesPerServing: 100));
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal",
            ("food", "party mix"), ("meal", "Snack"), ("recipe_name", "Party Mix"), ("servings", 20)));

        await Chat(client, journal, store).HandleAsync("all the party mix");

        Assert.Equal(2000, Assert.Single(journal.Drafts).Calories);
    }

    // --- query_journal --------------------------------------------------------------------------

    [Fact]
    public async Task Asking_about_the_week_answers_from_the_same_total_the_page_shows()
    {
        var journal = new FakeMealJournal();
        var week = MealJournal.SpanOf(JournalPeriod.Week, Today);
        journal.Entries.Add(new JournalEntry { Food = "Eggs", EatenOn = week.From, Calories = 180, CaloriesEstimated = true });
        journal.Entries.Add(new JournalEntry { Food = "Salad", EatenOn = Today, Calories = 320 });
        journal.Entries.Add(new JournalEntry { Food = "Last week", EatenOn = week.From.AddDays(-1), Calories = 9000 });
        var client = Script(Responses.Call("query_journal", ("period", "week")));

        await Chat(client, journal).HandleAsync("how many calories this week?");

        var expected = MealJournal.Describe(JournalPeriod.Week, week, MealJournal.Total(journal.Entries, week));
        Assert.Equal(expected, ToolReply(client));
        Assert.Contains("about 500 calories", expected);
    }

    [Fact]
    public async Task Asking_about_a_day_also_says_what_was_in_each_meal()
    {
        var journal = new FakeMealJournal();
        journal.Entries.Add(new JournalEntry { Food = "Eggs", EatenOn = Today, Slot = MealSlot.Breakfast, Calories = 180 });
        journal.Entries.Add(new JournalEntry { Food = "Toast", EatenOn = Today, Slot = MealSlot.Breakfast, Calories = 80 });
        journal.Entries.Add(new JournalEntry { Food = "Mystery stew", EatenOn = Today, Slot = MealSlot.Dinner });
        var client = Script(Responses.Call("query_journal", ("period", "Day")));

        await Chat(client, journal).HandleAsync("what did I eat today?");

        var reply = ToolReply(client);
        Assert.Contains("Breakfast: Eggs, Toast (260 kcal).", reply);
        Assert.Contains("Dinner: Mystery stew (no calorie count).", reply);
    }

    [Fact]
    public async Task A_month_question_for_another_date_reads_that_month()
    {
        var journal = new FakeMealJournal();
        journal.Entries.Add(new JournalEntry { Food = "Turkey", EatenOn = new DateOnly(2025, 11, 27), Calories = 1200 });
        var client = Script(Responses.Call("query_journal", ("period", "Month"), ("date", "2025-11-10")));

        await Chat(client, journal).HandleAsync("how much did I eat last November?");

        Assert.Equal("For November 2025: 1,200 calories across 1 item.", ToolReply(client));
    }

    [Fact]
    public async Task A_bad_period_or_date_is_refused()
    {
        var journal = new FakeMealJournal();
        var client = new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("query_journal", ("period", "Year"))),
            () => Responses.ToolCalls(Responses.Call("query_journal", ("period", "Day"), ("date", "yesterday"))),
            () => Responses.Text("Sorry."));

        await Chat(client, journal).HandleAsync("calories?");

        var first = client.ReceivedMessages[1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single();
        Assert.Contains("period must be one of", first.Result?.ToString());
        Assert.Contains("pass it as YYYY-MM-DD", ToolReply(client));
    }

    // --- the tool list is a promise -------------------------------------------------------------

    [Fact]
    public async Task The_journal_tools_are_offered_only_when_a_journal_is_wired()
    {
        // By count — every tool's Name reads "Tool" once wrapped (see the go_to_step gate's test).
        var without = new FakeChatClient(() => Responses.Text("Hi."));
        await Chat(without, journal: null).HandleAsync("hello");
        var with = new FakeChatClient(() => Responses.Text("Hi."));
        await Chat(with, new FakeMealJournal()).HandleAsync("hello");

        Assert.Equal(without.ReceivedOptions[0]!.Tools!.Count + 2, with.ReceivedOptions[0]!.Tools!.Count);
    }

    [Fact]
    public async Task A_journal_call_with_no_journal_wired_is_refused_rather_than_announced()
    {
        var client = new FakeChatClient(
            () => Responses.ToolCalls(
                Responses.Call("log_meal", ("food", "toast"), ("meal", "Breakfast")),
                Responses.Call("query_journal", ("period", "Day"))),
            () => Responses.Text("I can't do that here."));

        var result = await Chat(client, journal: null).HandleAsync("toast");

        Assert.Empty(result.Actions);
        Assert.Equal("The meal journal isn't set up. | The meal journal isn't set up.", ToolReply(client));
    }

    [Fact]
    public async Task The_clock_is_given_to_the_model_only_when_the_journal_needs_it()
    {
        var without = new FakeChatClient(() => Responses.Text("Hi."));
        await Chat(without, journal: null).HandleAsync("hello");
        var with = new FakeChatClient(() => Responses.Text("Hi."));
        await Chat(with, new FakeMealJournal()).HandleAsync("hello");

        static string System(FakeChatClient c) => c.ReceivedMessages[0][0].Text;
        Assert.DoesNotContain("The time now is", System(without));
        Assert.Contains("The time now is", System(with));
    }

    [Fact]
    public async Task The_journal_page_can_be_opened_by_voice()
    {
        var client = Script(Responses.Call("open_page", ("page", "journal")));

        var result = await Chat(client, new FakeMealJournal()).HandleAsync("show me my journal");

        Assert.Equal("/journal", result.NavigateTo);
    }

    // --- what it costs --------------------------------------------------------------------------

    [Fact]
    public async Task A_logged_meal_is_a_write_the_household_keeps_paying_for_even_if_the_provider_drops()
    {
        var charging = new ChargingChatClient(new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("log_meal", ("food", "toast"), ("meal", "Breakfast"), ("calories", 80))),
            () => throw new HttpRequestException("the provider went away")));

        var result = await Chat(charging, new FakeMealJournal()).HandleAsync("toast for breakfast");

        Assert.False(result.Success);
        Assert.True(charging.Charged);
        Assert.Null(charging.RefundedFor);
    }

    [Fact]
    public async Task A_refused_log_wrote_nothing_and_is_refunded_when_the_provider_drops()
    {
        var charging = new ChargingChatClient(new FakeChatClient(
            () => Responses.ToolCalls(Responses.Call("log_meal",
                ("food", "cake"), ("meal", "Dinner"), ("date", Today.AddDays(3).ToString("yyyy-MM-dd")))),
            () => throw new HttpRequestException("the provider went away")));

        await Chat(charging, new FakeMealJournal()).HandleAsync("cake next week");

        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }
}
