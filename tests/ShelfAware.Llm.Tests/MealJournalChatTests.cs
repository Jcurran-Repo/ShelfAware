using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
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
            (draft, var recalled) = MealJournal.Recall(draft, MealJournal.Remembered(Entries));
            var entry = new JournalEntry
            {
                Id = Entries.Count + 1, Food = draft.Food.Trim(), Slot = draft.Slot, EatenOn = draft.EatenOn,
                Calories = draft.Calories, CaloriesEstimated = draft.Calories is not null && draft.CaloriesEstimated,
                LoggedAt = DateTimeOffset.Now,
            };
            Entries.Add(entry);
            return Task.FromResult(JournalWrite.Saved(entry, recalled));
        }

        public Task<IReadOnlyList<RememberedCalories>> RememberedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(MealJournal.Remembered(Entries));

        public Task<JournalEntry?> GetEntryAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.Id == id));

        public List<(int Id, JournalDraft Draft)> Updates { get; } = [];

        public Task<JournalWrite> UpdateAsync(int id, JournalDraft draft, CancellationToken cancellationToken = default)
        {
            Updates.Add((id, draft));
            if (MealJournal.Problem(draft.Food, draft.Calories, draft.EatenOn, Today) is { } problem)
                return Task.FromResult(JournalWrite.Refused(problem));
            if (Entries.FirstOrDefault(e => e.Id == id) is not { } entry)
                return Task.FromResult(JournalWrite.Refused("That entry is no longer in your journal."));
            var updated = new JournalEntry
            {
                Id = id, Food = draft.Food.Trim(), Slot = draft.Slot, EatenOn = draft.EatenOn, Calories = draft.Calories,
                CaloriesEstimated = draft.Calories is not null && draft.CaloriesEstimated, LoggedAt = entry.LoggedAt,
            };
            Entries[Entries.IndexOf(entry)] = updated;
            return Task.FromResult(JournalWrite.Saved(updated));
        }

        /// <summary>Set to play a scope with nobody signed in.</summary>
        public string? Unreadable { get; set; }

        public Task<JournalRead> GetAsync(DateSpan span, CancellationToken cancellationToken = default) =>
            Task.FromResult(Unreadable is { } problem
                ? JournalRead.Refused(problem)
                : JournalRead.Of([.. Entries.Where(e => span.Contains(e.EatenOn))]));
    }

    private static AnthropicPantryChat Chat(IChatClient client, FakeMealJournal? journal, FakePantryStore? store = null) =>
        new(client, new LlmOptions(), store ?? new FakePantryStore(), NullLogger<AnthropicPantryChat>.Instance,
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
        var client = Script(Responses.Call("log_meal", ("food", "2 scrambled eggs"), ("meal", "Breakfast"), ("portion", "2"), ("calories", 180)));

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
        Assert.Contains("[entry 1] protein bar (250 kcal) for snack on", ToolReply(client));
        Assert.DoesNotContain("estimate", ToolReply(client));
    }

    [Fact]
    public async Task A_stated_date_is_used_and_today_is_the_default()
    {
        var journal = new FakeMealJournal();
        var yesterday = Today.AddDays(-1);
        var client = Script(
            Responses.Call("log_meal", ("food", "pizza"), ("meal", "Dinner"), ("portion", "2 slices"), ("calories", 600), ("date", yesterday.ToString("yyyy-MM-dd"))),
            Responses.Call("log_meal", ("food", "1 apple"), ("meal", "Snack"), ("portion", "1"), ("calories", 95)));

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
            ("food", "cake"), ("meal", "Dinner"), ("portion", "1 slice"), ("calories", 300), ("date", Today.AddDays(2).ToString("yyyy-MM-dd"))));

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
            ("food", "chili"), ("meal", "Dinner"), ("calories", 900),
            ("recipe_name", "chili"), ("servings", 1.5)));

        await Chat(client, journal, store).HandleAsync("I had a bowl and a half of the chicken chili");

        var draft = Assert.Single(journal.Drafts);
        Assert.Equal(630, draft.Calories);
        Assert.True(draft.CaloriesEstimated); // a recipe's figure is an estimate
        Assert.Contains("from the saved Chicken Chili recipe", ToolReply(client));
    }

    [Fact]
    public async Task A_number_the_user_said_outranks_the_saved_recipes_estimate()
    {
        // "chili, 700 calories" — the person weighed it or read a label; the recipe's figure is a guess.
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Chicken Chili", HasSteps: true, CaloriesPerServing: 420));
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal",
            ("food", "chili"), ("meal", "Dinner"), ("calories", 700), ("calories_stated", true),
            ("recipe_name", "chili"), ("servings", 1.5)));

        await Chat(client, journal, store).HandleAsync("I had the chicken chili, 700 calories");

        var draft = Assert.Single(journal.Drafts);
        Assert.Equal((700, false), (draft.Calories, draft.CaloriesEstimated));
        Assert.DoesNotContain("from the saved", ToolReply(client));
    }

    [Fact]
    public async Task Stated_with_no_number_is_not_a_stated_number_so_the_recipe_still_prices_it()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Chicken Chili", HasSteps: true, CaloriesPerServing: 420));
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal",
            ("food", "chili"), ("meal", "Dinner"), ("servings", 1), ("calories_stated", true), ("recipe_name", "chili")));

        await Chat(client, journal, store).HandleAsync("I had the chicken chili");

        var draft = Assert.Single(journal.Drafts);
        Assert.Equal((420, true), (draft.Calories, draft.CaloriesEstimated));
    }

    [Fact]
    public async Task A_recipe_with_no_figure_or_no_match_keeps_the_models_estimate()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Chicken Chili", HasSteps: true, CaloriesPerServing: null));
        var journal = new FakeMealJournal();
        var client = Script(
            Responses.Call("log_meal", ("food", "chili"), ("meal", "Dinner"), ("portion", "a bowl"), ("calories", 500), ("recipe_name", "chicken chili")),
            Responses.Call("log_meal", ("food", "tacos"), ("meal", "Dinner"), ("portion", "3"), ("calories", 450), ("recipe_name", "fish tacos")));

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

    // --- asking the portion ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task With_no_portion_nothing_is_logged_and_the_model_is_told_to_ask(string? portion)
    {
        // The 7 Brew case: "a white chocolate mocha chiller" says nothing about a 32 oz cup.
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal",
            ("food", "7 Brew white chocolate mocha chiller"), ("meal", "Snack"), ("calories", 450), ("portion", portion)));

        var result = await Chat(client, journal).HandleAsync("had a white chocolate mocha chiller from 7 brew");

        Assert.Empty(journal.Drafts);
        Assert.Empty(result.Actions);
        Assert.Equal(AnthropicPantryChat.AskPortion, ToolReply(client));
    }

    [Fact]
    public async Task A_portion_in_their_words_is_enough_to_log()
    {
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal",
            ("food", "7 Brew white chocolate mocha chiller, large 32 oz"), ("meal", "Snack"), ("calories", 900), ("portion", "large")));

        await Chat(client, journal).HandleAsync("a large white chocolate mocha chiller");

        Assert.Equal(900, Assert.Single(journal.Drafts).Calories);
    }

    [Fact]
    public async Task The_portion_they_said_becomes_part_of_the_food_unless_the_name_already_says_it()
    {
        var journal = new FakeMealJournal();
        var client = Script(
            Responses.Call("log_meal", ("food", "7 Brew chiller"), ("meal", "Snack"), ("calories", 900), ("portion", "32 oz")),
            Responses.Call("log_meal", ("food", "2 scrambled eggs"), ("meal", "Breakfast"), ("calories", 180), ("portion", "2")));

        await Chat(client, journal).HandleAsync("a 32 oz chiller and two eggs");

        Assert.Equal(["7 Brew chiller, 32 oz", "2 scrambled eggs"], journal.Drafts.Select(d => d.Food));
    }

    [Fact]
    public async Task A_size_they_said_is_not_overridden_by_another_sizes_remembered_number()
    {
        var journal = new FakeMealJournal();
        journal.Entries.Add(Own("chiller", 900)); // the large, before sizes were in the name
        var client = Script(Responses.Call("log_meal", ("food", "chiller"), ("meal", "Snack"), ("calories", 450), ("portion", "small")));

        await Chat(client, journal).HandleAsync("a small chiller");

        Assert.Equal(("chiller, small", 450, true), (journal.Entries[^1].Food, journal.Entries[^1].Calories, journal.Entries[^1].CaloriesEstimated));
    }

    [Fact]
    public async Task Servings_only_excuse_the_portion_when_a_saved_recipe_priced_them()
    {
        // No recipe, or one with no calorie figure: the servings are ignored, so the model's guess would be
        // what gets written — exactly the guess the question exists to stop.
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Plain Rice", HasSteps: false, CaloriesPerServing: null));
        var journal = new FakeMealJournal();
        var client = Script(
            Responses.Call("log_meal", ("food", "chiller"), ("meal", "Snack"), ("calories", 450), ("servings", 1)),
            Responses.Call("log_meal", ("food", "rice"), ("meal", "Dinner"), ("calories", 300), ("servings", 1), ("recipe_name", "Plain Rice")));

        await Chat(client, journal, store).HandleAsync("a chiller, and some rice");

        Assert.Empty(journal.Drafts);
        Assert.Equal($"{AnthropicPantryChat.AskPortion} | {AnthropicPantryChat.AskPortion}", ToolReply(client));
    }

    [Fact]
    public async Task A_saved_recipe_with_no_servings_said_is_asked_about_too()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Chicken Chili", HasSteps: true, CaloriesPerServing: 420));
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal", ("food", "chili"), ("meal", "Dinner"), ("portion", "a bowl"), ("recipe_name", "chili")));

        await Chat(client, journal, store).HandleAsync("I had the chili");

        Assert.Empty(journal.Drafts);
        Assert.Equal(AnthropicPantryChat.AskPortion, ToolReply(client));
    }

    [Fact]
    public async Task Saying_stated_without_a_number_does_not_excuse_the_portion()
    {
        var journal = new FakeMealJournal();
        var client = Script(Responses.Call("log_meal", ("food", "latte"), ("meal", "Breakfast"), ("calories_stated", true)));

        await Chat(client, journal).HandleAsync("a latte");

        Assert.Empty(journal.Drafts);
        Assert.Equal(AnthropicPantryChat.AskPortion, ToolReply(client));
    }

    [Fact]
    public async Task A_food_they_have_priced_before_needs_no_portion()
    {
        var journal = new FakeMealJournal();
        journal.Entries.Add(Own("7 Brew white chocolate mocha chiller, large 32 oz", 900));
        var client = Script(Responses.Call("log_meal",
            ("food", "7 brew white chocolate mocha chiller, LARGE 32 oz"), ("meal", "Snack")));

        await Chat(client, journal).HandleAsync("had my usual chiller");

        Assert.Equal((900, false), (journal.Entries[^1].Calories, journal.Entries[^1].CaloriesEstimated));
    }

    // --- remembered numbers ---------------------------------------------------------------------

    private static JournalEntry Own(string food, int calories) => new()
    {
        Id = 1, Food = food, Slot = MealSlot.Snack, EatenOn = Today.AddDays(-1), Calories = calories,
        CaloriesEstimated = false, LoggedAt = DateTimeOffset.Now,
    };

    [Fact]
    public async Task The_persons_own_numbers_are_listed_for_the_model_by_the_names_their_journal_uses()
    {
        var journal = new FakeMealJournal();
        journal.Entries.Add(Own("7 Brew Blondie, medium 24 oz", 480));
        var client = new FakeChatClient(() => Responses.Text("Hi."));

        await Chat(client, journal).HandleAsync("hello");

        var system = client.ReceivedMessages[0][0].Text;
        Assert.Contains("Foods the user has given their own calorie number for", system);
        Assert.Contains("- 7 Brew Blondie, medium 24 oz: 480 kcal", system);
    }

    [Fact]
    public async Task With_nothing_remembered_the_prompt_lists_nothing()
    {
        var client = new FakeChatClient(() => Responses.Text("Hi."));

        await Chat(client, new FakeMealJournal()).HandleAsync("hello");

        // The standing prompt mentions the list; the list itself (its header) is only there when there is one.
        Assert.DoesNotContain("Foods the user has given their own calorie number for", client.ReceivedMessages[0][0].Text);
    }

    [Fact]
    public async Task A_remembered_number_outranks_the_models_estimate_and_the_model_is_told_whose_it_is()
    {
        var journal = new FakeMealJournal();
        journal.Entries.Add(Own("7 Brew Blondie, medium 24 oz", 480));
        var client = Script(Responses.Call("log_meal",
            ("food", "7 Brew Blondie, medium 24 oz"), ("meal", "Snack"), ("calories", 240)));

        await Chat(client, journal).HandleAsync("had a blondie from 7 brew");

        var entry = journal.Entries[^1];
        Assert.Equal((480, false), (entry.Calories, entry.CaloriesEstimated));
        Assert.Contains("(480 kcal) for snack on", ToolReply(client));
        Assert.Contains("(their own number from the last time they logged it).", ToolReply(client));
        Assert.DoesNotContain("estimate", ToolReply(client));
    }

    [Fact]
    public async Task A_remembered_number_outranks_a_saved_recipes_figure_and_says_so_instead()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Chicken Chili", HasSteps: true, CaloriesPerServing: 420));
        var journal = new FakeMealJournal();
        journal.Entries.Add(Own("chili", 650));
        var client = Script(Responses.Call("log_meal", ("food", "chili"), ("meal", "Dinner"), ("recipe_name", "chili")));

        await Chat(client, journal, store).HandleAsync("I had the chili");

        Assert.Equal(650, journal.Entries[^1].Calories);
        Assert.Contains("their own number", ToolReply(client));
        Assert.DoesNotContain("from the saved", ToolReply(client));
    }

    [Fact]
    public async Task Servings_other_than_one_are_part_of_the_food_so_one_bowls_number_is_not_recalled_for_three()
    {
        var store = new FakePantryStore();
        store.Recipes.Add(new RecipeRef(7, "Chicken Chili", HasSteps: true, CaloriesPerServing: 420));
        var journal = new FakeMealJournal();
        journal.Entries.Add(Own("chili", 400));
        var client = Script(
            Responses.Call("log_meal", ("food", "chili"), ("meal", "Dinner"), ("recipe_name", "chili"), ("servings", 3)),
            Responses.Call("log_meal", ("food", "chili"), ("meal", "Lunch"), ("recipe_name", "chili"), ("servings", 1.5)));

        await Chat(client, journal, store).HandleAsync("three bowls of chili for dinner, and one and a half for lunch");

        Assert.Equal([("Chicken Chili, 3 servings", 1260), ("Chicken Chili, 1.5 servings", 630)],
            journal.Entries.Skip(1).Select(e => (e.Food, e.Calories!.Value)));
    }

    // --- edit_meal -----------------------------------------------------------------------------

    private static FakeMealJournal WithChiller(int calories = 450, bool estimated = true)
    {
        var journal = new FakeMealJournal();
        journal.Entries.Add(new JournalEntry
        {
            Id = 7, Food = "7 Brew chiller, 16 oz", Slot = MealSlot.Snack, EatenOn = Today, Calories = calories,
            CaloriesEstimated = estimated, LoggedAt = DateTimeOffset.Now,
        });
        return journal;
    }

    [Fact]
    public async Task A_new_size_is_an_edit_of_the_entry_not_a_second_log()
    {
        var journal = WithChiller();
        var client = Script(Responses.Call("edit_meal",
            ("entry_id", 7), ("food", "7 Brew chiller"), ("portion", "32 oz"), ("calories", 900)));

        var result = await Chat(client, journal).HandleAsync("actually it was the 32 oz");

        Assert.Empty(journal.Drafts);
        var entry = Assert.Single(journal.Entries);
        Assert.Equal(("7 Brew chiller, 32 oz", 900, true, MealSlot.Snack, Today),
            (entry.Food, entry.Calories, entry.CaloriesEstimated, entry.Slot, entry.EatenOn));
        Assert.Contains("journal ✎ 7 Brew chiller, 32 oz", result.Actions);
        Assert.Equal($"Updated [entry 7] 7 Brew chiller, 16 oz (~450 kcal) to [entry 7] 7 Brew chiller, 32 oz (~900 kcal), snack on {Today:yyyy-MM-dd} — an estimate.",
            ToolReply(client));
    }

    [Fact]
    public async Task A_number_they_state_for_an_entry_is_theirs()
    {
        var journal = WithChiller();
        var client = Script(Responses.Call("edit_meal", ("entry_id", 7), ("calories", 500), ("calories_stated", true)));

        await Chat(client, journal).HandleAsync("make that 500 calories");

        var entry = Assert.Single(journal.Entries);
        Assert.Equal(("7 Brew chiller, 16 oz", 500, false), (entry.Food, entry.Calories, entry.CaloriesEstimated));
        Assert.EndsWith("snack on " + Today.ToString("yyyy-MM-dd") + ".", ToolReply(client));
    }

    [Fact]
    public async Task Moving_an_entry_keeps_its_number_and_whose_it_is()
    {
        var journal = WithChiller(calories: 480, estimated: false);
        var yesterday = Today.AddDays(-1);
        var client = Script(Responses.Call("edit_meal", ("entry_id", 7), ("meal", "lunch"), ("date", yesterday.ToString("yyyy-MM-dd"))));

        await Chat(client, journal).HandleAsync("that was lunch yesterday");

        var entry = Assert.Single(journal.Entries);
        Assert.Equal((MealSlot.Lunch, yesterday, 480, false), (entry.Slot, entry.EatenOn, entry.Calories, entry.CaloriesEstimated));
    }

    [Fact]
    public async Task A_different_food_with_no_new_number_is_refused_unless_they_have_one_for_it()
    {
        // The old food's calories under the new food's name would be a number nobody gave.
        var journal = WithChiller();
        var client = Script(Responses.Call("edit_meal", ("entry_id", 7), ("portion", "32 oz"), ("food", "7 Brew chiller")));

        await Chat(client, journal).HandleAsync("it was the 32");

        Assert.Empty(journal.Updates);
        Assert.StartsWith("Not changed: the food is different now", ToolReply(client));

        journal.Entries.Add(new JournalEntry
        {
            Id = 8, Food = "7 Brew chiller, 32 oz", Slot = MealSlot.Snack, EatenOn = Today.AddDays(-3), Calories = 910,
            LoggedAt = DateTimeOffset.Now,
        });
        var again = Script(Responses.Call("edit_meal", ("entry_id", 7), ("portion", "32 oz"), ("food", "7 Brew chiller")));

        await Chat(again, journal).HandleAsync("it was the 32");

        Assert.Equal((910, false), (journal.Entries.Single(e => e.Id == 7).Calories, journal.Entries.Single(e => e.Id == 7).CaloriesEstimated));
    }

    [Theory]
    [InlineData(null, "Pass entry_id")]
    [InlineData(99, "There is no entry 99 in their journal")]
    public async Task An_edit_needs_an_entry_that_is_there(int? id, string expected)
    {
        var journal = WithChiller();
        var client = Script(Responses.Call("edit_meal", ("entry_id", id), ("calories", 500)));

        var result = await Chat(client, journal).HandleAsync("make it 500");

        Assert.Empty(journal.Updates);
        Assert.Empty(result.Actions);
        Assert.StartsWith(expected, ToolReply(client));
    }

    [Theory]
    [InlineData("meal", "Brunch", "meal must be one of")]
    [InlineData("date", "last Tuesday", "pass it as YYYY-MM-DD")]
    public async Task An_unreadable_meal_or_day_is_refused_rather_than_guessed(string key, string value, string expected)
    {
        var journal = WithChiller();
        var client = Script(Responses.Call("edit_meal", ("entry_id", 7), (key, value)));

        await Chat(client, journal).HandleAsync("move it");

        Assert.Empty(journal.Updates);
        Assert.Contains(expected, ToolReply(client));
    }

    [Fact]
    public async Task The_write_rule_still_applies_to_an_edit()
    {
        var journal = WithChiller();
        var client = Script(Responses.Call("edit_meal", ("entry_id", 7), ("date", Today.AddDays(1).ToString("yyyy-MM-dd"))));

        var result = await Chat(client, journal).HandleAsync("that's for tomorrow");

        Assert.Empty(result.Actions);
        Assert.Contains("hasn't happened yet", ToolReply(client));
        Assert.Equal(Today, Assert.Single(journal.Entries).EatenOn);
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
        journal.Entries.Add(new JournalEntry { Id = 1, Food = "Eggs", EatenOn = Today, Slot = MealSlot.Breakfast, Calories = 180 });
        journal.Entries.Add(new JournalEntry { Id = 2, Food = "Toast", EatenOn = Today, Slot = MealSlot.Breakfast, Calories = 80 });
        journal.Entries.Add(new JournalEntry { Id = 3, Food = "Mystery stew", EatenOn = Today, Slot = MealSlot.Dinner });
        var client = Script(Responses.Call("query_journal", ("period", "Day")));

        await Chat(client, journal).HandleAsync("what did I eat today?");

        var reply = ToolReply(client);
        // Each food by its [entry N], so "change the toast" can name the row edit_meal takes.
        Assert.Contains("Breakfast: [entry 1] Eggs (180 kcal), [entry 2] Toast (80 kcal) — 260 kcal.", reply);
        Assert.Contains("Dinner: [entry 3] Mystery stew (no calorie count) — no calorie count.", reply);
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
    public async Task With_nobody_to_read_for_the_answer_is_the_refusal_not_nothing_logged()
    {
        var journal = new FakeMealJournal { Unreadable = "Nobody is signed in." };
        var client = Script(Responses.Call("query_journal", ("period", "day")));

        await Chat(client, journal).HandleAsync("what did I eat today?");

        Assert.Equal("Nobody is signed in.", ToolReply(client));
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

        Assert.Equal(without.ReceivedOptions[0]!.Tools!.Count + 3, with.ReceivedOptions[0]!.Tools!.Count);
    }

    [Fact]
    public async Task A_journal_call_with_no_journal_wired_is_refused_rather_than_announced()
    {
        var client = new FakeChatClient(
            () => Responses.ToolCalls(
                Responses.Call("log_meal", ("food", "toast"), ("meal", "Breakfast")),
                Responses.Call("query_journal", ("period", "Day")),
                Responses.Call("edit_meal", ("entry_id", 1), ("calories", 100))),
            () => Responses.Text("I can't do that here."));

        var result = await Chat(client, journal: null).HandleAsync("toast");

        Assert.Empty(result.Actions);
        Assert.Equal("The meal journal isn't set up. | The meal journal isn't set up. | The meal journal isn't set up.", ToolReply(client));
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
            () => Responses.ToolCalls(Responses.Call("log_meal", ("food", "toast"), ("meal", "Breakfast"), ("portion", "2 slices"), ("calories", 80))),
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
                ("food", "cake"), ("meal", "Dinner"), ("portion", "1 slice"), ("date", Today.AddDays(3).ToString("yyyy-MM-dd")))),
            () => throw new HttpRequestException("the provider went away")));

        await Chat(charging, new FakeMealJournal()).HandleAsync("cake next week");

        Assert.True(charging.Charged);
        Assert.Equal(0, charging.RefundedFor);
    }
}
