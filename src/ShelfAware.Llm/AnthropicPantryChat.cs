using System.Reflection;
using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Ingest;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using Category = ShelfAware.Core.Domain.Category;

namespace ShelfAware.Llm;

/// <summary>
/// <see cref="IPantryChat"/> over the Anthropic Messages API with a manual tool-calling loop
/// (DESIGN.md §7, Option B). Consistent with the project's choice to use the SDK directly behind
/// an interface (see <see cref="AnthropicReceiptExtractor"/>) rather than Semantic Kernel.
/// </summary>
public class AnthropicPantryChat : IPantryChat
{
    private const int MaxTurns = 5;
    private static readonly string SystemPrompt = ReadEmbedded("Prompts.pantry-chat-system.txt");

    private readonly IChatClient _chat;
    private readonly LlmOptions _options;
    private readonly IPantryStore _store;
    private readonly IReceiptImporter? _importer;
    private readonly IProductSubstituteAdvisor? _substituteAdvisor;
    private readonly IRecipeAdapter? _recipeAdapter;
    private readonly IRecipeAdvisor? _recipeAdvisor;
    private readonly IAppSettings? _settings;
    private readonly ILogger<AnthropicPantryChat> _logger;

    public AnthropicPantryChat(
        IChatClient chat, IOptions<LlmOptions> options, IPantryStore store, ILogger<AnthropicPantryChat> logger,
        IReceiptImporter? importer = null, IProductSubstituteAdvisor? substituteAdvisor = null,
        IRecipeAdapter? recipeAdapter = null, IRecipeAdvisor? recipeAdvisor = null, IAppSettings? settings = null)
    {
        _chat = chat;
        _options = options.Value;
        _store = store;
        _importer = importer;
        _substituteAdvisor = substituteAdvisor;
        _recipeAdapter = recipeAdapter;
        _recipeAdvisor = recipeAdvisor;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ChatResult> HandleAsync(
        string userText, IReadOnlyList<ChatTurn>? history = null, string? screenContext = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) return ChatResult.Fail("Type something to update.");

        var products = await _store.GetProductsAsync(cancellationToken);
        var knownTags = await _store.GetKnownTagsAsync(cancellationToken);
        var system = SystemPrompt + "\n\nCurrent products:\n" + (products.Count == 0
            ? "(none yet)"
            : string.Join("\n", products.OrderBy(p => p.Name).Select(p => $"- {p.Name} ({p.Category})")));
        if (knownTags.Count > 0)
            system += "\n\nKnown tags (reuse one of these when tagging; coin a new tag only when none fits):\n"
                + string.Join(", ", knownTags);
        // What the user is looking at right now, so on-screen references ("the second one") resolve.
        if (!string.IsNullOrWhiteSpace(screenContext))
            system += "\n\nOn screen right now:\n" + screenContext.Trim();

        var chatOptions = new ChatOptions
        {
            ModelId = _options.ChatModel,
            MaxOutputTokens = 1024,
            Tools = BuildTools(),
        };
        // Replay prior (user, assistant) exchanges so follow-ups resolve against what was just said,
        // then append the new user turn. Empty history = the original single-turn behaviour.
        var messages = new List<ChatMessage> { new(ChatRole.System, system) };
        if (history is { Count: > 0 })
        {
            foreach (var turn in history)
            {
                messages.Add(new ChatMessage(ChatRole.User, turn.User));
                messages.Add(new ChatMessage(ChatRole.Assistant, turn.Assistant));
            }
        }
        messages.Add(new ChatMessage(ChatRole.User, userText));

        var actions = new List<string>();
        var nav = new NavigationTarget(); // set by open_page / read_recipe; carried out on ChatResult

        for (var turn = 0; turn < MaxTurns; turn++)
        {
            ChatResponse response;
            try
            {
                response = await _chat.GetResponseAsync(messages, chatOptions, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // the caller cancelled (e.g. circuit gone) — not a model failure
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pantry chat call to the model failed on turn {Turn}.", turn + 1);
                return ChatResult.Fail($"Sorry — I couldn't reach the assistant just now. ({ex.Message})");
            }

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                var text = response.Text.Trim();
                _logger.LogInformation("Pantry chat completed on turn {Turn} with {ActionCount} action(s) applied.", turn + 1, actions.Count);
                return ChatResult.Ok(text.Length > 0 ? text : "Done.", actions, nav.Url, nav.HandsOff);
            }

            // Carry the assistant's tool-call turn back into the history, then answer each call.
            messages.AddRange(response.Messages);

            var results = new List<AIContent>();
            foreach (var call in calls)
            {
                string text;
                try
                {
                    (text, _) = await ExecuteToolAsync(call, products, actions, nav, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw; // caller cancelled (e.g. circuit gone) — not a tool failure
                }
                catch (Exception ex)
                {
                    // A tool handler threw (e.g. a DB write failed). Feed the model an error result so the
                    // loop stays resilient — it can retry or explain — instead of the exception escaping the
                    // whole chat call and blanking whichever surface invoked it (the dashboard box / push-to-talk
                    // don't wrap HandleAsync). Expected/validation problems already return as tuple text; this
                    // is only for genuine throws.
                    _logger.LogError(ex, "Pantry chat tool {Tool} threw.", call.Name);
                    text = $"That step ({call.Name}) hit an error and couldn't be completed.";
                }
                results.Add(new FunctionResultContent(call.CallId, text));
            }
            messages.Add(new ChatMessage(ChatRole.Tool, results));

            // create_product may have added rows — refresh so later fuzzy matches see them.
            products = await _store.GetProductsAsync(cancellationToken);
        }

        _logger.LogWarning("Pantry chat hit the {MaxTurns}-turn limit without a final reply ({ActionCount} action(s) applied).", MaxTurns, actions.Count);
        return ChatResult.Ok(
            actions.Count > 0 ? $"Applied: {string.Join(", ", actions)}." : "Stopped after several steps without finishing.",
            actions, nav.Url, nav.HandsOff);
    }

    /// <summary>Mutable navigation slot the tool handlers write into (last navigation wins) — the
    /// service is a singleton, so per-request state rides through parameters, never fields.
    /// <see cref="HandsOff"/> marks a navigation that starts its own audio on the destination
    /// (read_recipe), so a persistent listening agent knows to stop rather than talk over it.</summary>
    private sealed class NavigationTarget { public string? Url; public bool HandsOff; }

    private async Task<(string text, bool isError)> ExecuteToolAsync(
        FunctionCallContent call, IReadOnlyList<Product> products, List<string> actions, NavigationTarget nav, CancellationToken ct)
    {
        string? Str(string key) => call.Arguments is { } a && a.TryGetValue(key, out var v) ? AsString(v) : null;
        decimal? Dec(string key) => call.Arguments is { } a && a.TryGetValue(key, out var v) ? AsDecimal(v) : null;
        int? Int(string key) => Dec(key) is { } d ? (int)d : null;
        bool? Bool(string key) => call.Arguments is { } a && a.TryGetValue(key, out var v) ? AsBool(v) : null;
        List<string>? StrList(string key) => call.Arguments is { } a && a.TryGetValue(key, out var v) ? AsStringList(v) : null;

        switch (call.Name)
        {
            case "record_signal":
            {
                var name = Str("product_name");
                if (!Enum.TryParse<SignalKind>(Str("kind"), ignoreCase: true, out var kind))
                    return ("Invalid 'kind' — use OutNow, RunningLow, or Restocked.", true);
                var product = ProductMatcher.Resolve(name, products);
                if (product is null)
                    return ($"No product matches \"{name}\". Call create_product first if it's new.", true);
                await _store.RecordSignalAsync(product.Id, kind, ct);
                actions.Add($"{kind} → {product.Name}");
                return ($"Recorded {kind} for {product.Name}.", false);
            }

            case "add_purchase":
            {
                var name = Str("product_name");
                var product = ProductMatcher.Resolve(name, products);
                if (product is null)
                    return ($"No product matches \"{name}\". Call create_product first if it's new.", true);
                var date = DateOnly.TryParse(Str("date"), out var d) ? d : DateOnly.FromDateTime(DateTime.Today);
                var qty = Dec("quantity") is { } q && q > 0 ? q : 1m;
                var retracked = await _store.AddPurchaseAsync(product.Id, date, qty, ct);
                actions.Add($"purchase → {product.Name}");
                return ($"Logged {qty:0.##} × {product.Name} on {date:yyyy-MM-dd}." +
                    (retracked ? " It was untracked; this purchase resumed tracking — mention that to the user." : ""), false);
            }

            case "query_status":
            {
                var name = Str("product_name");
                var today = DateOnly.FromDateTime(DateTime.Today);
                var nameById = products.ToDictionary(p => p.Id, p => p.Name);

                if (string.IsNullOrWhiteSpace(name))
                {
                    var low = products.Where(p => p.IsTracked)
                        .Select(p => ReplenishmentPredictor.Predict(p, today))
                        .Where(r => r.Status is PredictionStatus.Overdue or PredictionStatus.DueSoon)
                        .OrderByDescending(r => r.Status)
                        .ToList();
                    if (low.Count == 0) return ("Nothing is running low right now.", false);
                    var list = string.Join("; ", low.Select(r => $"{nameById[r.ProductId]} — {r.Status} ({r.Basis})"));
                    return ($"Running low: {list}.", false);
                }

                var product = ProductMatcher.Resolve(name, products);
                if (product is null) return ($"No product matches \"{name}\".", true);
                var pr = ReplenishmentPredictor.Predict(product, today);
                var due = pr.DueDate is { } dd ? $", due {dd:yyyy-MM-dd}" : "";
                return ($"{product.Name}: {pr.Status} ({pr.Basis}){due}.", false);
            }

            case "set_tracking":
            {
                var name = Str("product_name");
                var product = ProductMatcher.Resolve(name, products);
                if (product is null)
                    return ($"No product matches \"{name}\".", true);
                var tracked = Bool("tracked") ?? false;
                await _store.SetTrackingAsync(product.Id, tracked, ct);
                actions.Add($"{(tracked ? "tracking" : "untracked")} → {product.Name}");
                return ($"{(tracked ? "Now tracking" : "Stopped tracking")} {product.Name}.", false);
            }

            case "create_product":
            {
                var name = Str("name")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) return ("A product name is required.", true);
                if (!Enum.TryParse<Category>(Str("category"), ignoreCase: true, out var category))
                    category = Category.Other;
                var existing = products.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) return ($"\"{existing.Name}\" already exists — use it instead.", false);
                var tags = StrList("tags") ?? [];
                await _store.CreateProductAsync(name, category, tags, ct);
                actions.Add($"created {name}");
                return ($"Created {name} ({category}){(tags.Count > 0 ? $", tagged {string.Join(", ", tags)}" : "")}.", false);
            }

            case "import_receipts":
            {
                if (_importer is null)
                    return ("Receipt import isn't set up.", true);
                var summary = await _importer.ImportNewAsync(cancellationToken: ct);
                if (summary.Imported > 0) actions.Add($"imported {summary.Imported} receipt(s)");
                return (summary.Describe(), false);
            }

            case "suggest_substitutes":
            {
                var name = Str("product_name");
                var product = ProductMatcher.Resolve(name, products);
                if (product is null)
                    return ($"No product matches \"{name}\".", true);
                // User-stated phrases ("the wagyu works as ground beef") are saved verbatim — the whole
                // point is honoring what THEY said; the advisor only fills in when nothing was stated.
                IReadOnlyList<string> ideas;
                if (StrList("substitutes") is { Count: > 0 } stated)
                {
                    ideas = stated;
                }
                else
                {
                    if (_substituteAdvisor is null)
                        return ("Substitute suggestions aren't set up.", true);
                    ideas = await _substituteAdvisor.SuggestAsync(product.Name, product.Category.ToString(), ct);
                    if (ideas.Count == 0)
                        return ($"Couldn't think of any substitutes for {product.Name}.", false);
                }
                var added = await _store.AddSubstitutesAsync(product.Id, ideas, ct);
                if (added.Count > 0) actions.Add($"substitutes → {product.Name}");
                return (added.Count > 0
                    ? $"Added \"also works as\" for {product.Name}: {string.Join(", ", added)}."
                    : $"{product.Name} already has those substitutes.", false);
            }

            case "add_tags":
            {
                var name = Str("product_name");
                var product = ProductMatcher.Resolve(name, products);
                if (product is null)
                    return ($"No product matches \"{name}\".", true);
                var tags = StrList("tags") ?? [];
                if (tags.Count == 0) return ("Pass at least one tag.", true);
                var added = await _store.AddTagsAsync(product.Id, tags, ct);
                if (added.Count > 0) actions.Add($"tags → {product.Name}");
                return (added.Count > 0
                    ? $"Tagged {product.Name}: {string.Join(", ", added)}."
                    : $"{product.Name} already has those tags (or near-duplicates of them).", false);
            }

            case "add_recipe_to_list":
            {
                if (_recipeAdvisor is null)
                    return ("Recipe ideas aren't set up.", true);
                var request = Str("recipe")?.Trim();
                if (string.IsNullOrWhiteSpace(request))
                    return ("Which dish should I add the ingredients for?", true);
                var includeSeasonings = Bool("include_seasonings") ?? false;
                var confirmed = Bool("confirmed") ?? false;

                // Generate the recipe grounded on what's on hand (prefers existing products) and hard-
                // excluding won't-eat foods — both are the advisor's job, unchanged.
                var today = DateOnly.FromDateTime(DateTime.Today);
                var onHand = PantryOnHand.EdibleInStock(products, today).Select(p => p.Name).ToList();
                var excluded = await _store.GetExcludedFoodsAsync(ct);
                var recipe = (await _recipeAdvisor.SuggestAsync(request, onHand, excluded, ct)).FirstOrDefault();
                if (recipe is null)
                    return ($"I couldn't come up with a {request} recipe just now.", false);

                // Buy only what they don't already have (Have = the model matched it to an on-hand product);
                // mains always, seasonings only if asked. Prefer-existing is automatic — a matched item is skipped.
                var missing = recipe.Ingredients
                    .Where(i => !i.Have && (i.IsMain || includeSeasonings))
                    .Select(i => i.Name)
                    .ToList();
                if (missing.Count == 0)
                    return ($"You already have everything for {recipe.Name}.", false);

                // Respect the setting: Confirm (default) proposes first and adds only once the user agrees
                // (a follow-up call with confirmed=true); Auto adds straight away.
                var mode = _settings is null ? "Confirm" : await _settings.GetAsync(SettingKeys.RecipeAddConfirm, ct) ?? "Confirm";
                if (!string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase) && !confirmed)
                    return ($"For {recipe.Name} you'd need: {string.Join(", ", missing)}. Want me to add {(missing.Count == 1 ? "it" : "them")} to your grocery list?", false);

                // A shopping-list add is NOT an "I'm out" signal — extras only, never RecordSignal (keeps the
                // burn-rate/rebuy prediction honest).
                var addedToList = await _store.AddGroceryExtrasAsync(missing, ct);
                if (addedToList.Count > 0) actions.Add($"list += {addedToList.Count} for {recipe.Name}");
                return (addedToList.Count > 0
                    ? $"Added {string.Join(", ", addedToList)} to your grocery list for {recipe.Name}."
                    : $"Everything for {recipe.Name} was already on your list.", false);
            }

            case "open_page":
            {
                var page = Str("page")?.Trim().ToLowerInvariant();
                if (page == "product")
                {
                    var name = Str("product_name");
                    var product = ProductMatcher.Resolve(name, products);
                    if (product is null)
                        return ($"No product matches \"{name}\".", true);
                    nav.Url = $"/product/{product.Id}";
                    actions.Add($"opened {product.Name}");
                    return ($"Opening {product.Name}.", false);
                }
                // "recipes that use the chicken" — the recipes page, scoped to one product. Same
                // fuzzy product resolution as page="product"; the Recipes page does the filtering.
                if (page == "recipes" && Str("product_name") is { Length: > 0 } forName)
                {
                    var product = ProductMatcher.Resolve(forName, products);
                    if (product is null)
                        return ($"No product matches \"{forName}\".", true);
                    nav.Url = $"/recipes?uses={product.Id}";
                    actions.Add($"opened recipes using {product.Name}");
                    return ($"Showing recipes that use {product.Name}.", false);
                }
                string? url = page switch
                {
                    "dashboard" or "home" => "/",
                    "grocery_list" or "list" => "/list",
                    "recipes" => "/recipes",
                    "trends" => "/trends",
                    "upload" or "receipt" => "/receipt",
                    "receipts" => "/receipts",
                    "products" => "/products",
                    "accuracy" => "/accuracy",
                    "settings" => "/settings",
                    _ => null,
                };
                if (url is null) return ($"Unknown page '{page}'.", true);
                nav.Url = url;
                actions.Add($"opened {page!.Replace('_', ' ')}");
                return ($"Opening the {page.Replace('_', ' ')} page.", false);
            }

            case "read_recipe":
            {
                var name = Str("recipe_name")?.Trim();
                var position = Int("position");
                if (string.IsNullOrWhiteSpace(name) && position is null)
                    return ("A recipe name or position is required.", true);
                var recipes = await _store.GetRecipesAsync(ct);
                if (recipes.Count == 0)
                    return ("There are no saved recipes yet — save one on the Recipes page first.", true);
                // Position wins when given: it's the explicit "the second one" case, and the store's
                // list is in the exact order the Recipes page displays (newest saved first).
                RecipeRef? match;
                if (position is { } pos)
                {
                    if (pos < 1 || pos > recipes.Count)
                        return ($"There {(recipes.Count == 1 ? "is only 1 saved recipe" : $"are only {recipes.Count} saved recipes")} — position {pos} doesn't exist. In order: {string.Join("; ", recipes.Select(r => r.Name))}.", true);
                    match = recipes[pos - 1];
                }
                else
                {
                    match = ResolveRecipe(name!, recipes);
                }
                if (match is null)
                    // Feed the real names back so the model can self-correct or ask which one was meant.
                    return ($"No saved recipe matches \"{name}\". Saved recipes: {string.Join("; ", recipes.Select(r => r.Name))}.", true);
                if (!match.HasSteps)
                    return ($"\"{match.Name}\" has no cooking steps saved, so there's nothing to read aloud.", true);
                nav.Url = $"/recipes?read={match.Id}";
                nav.HandsOff = true; // the reader produces its own audio — the listening agent stands down
                actions.Add($"reading {match.Name}");
                return ($"Opening {match.Name} and reading it aloud.", false);
            }

            case "adapt_recipe":
            {
                if (_recipeAdapter is null)
                    return ("Recipe adapting isn't set up.", true);
                var name = Str("recipe_name")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) return ("A recipe name is required.", true);
                var recipes = await _store.GetRecipesAsync(ct);
                if (recipes.Count == 0)
                    return ("There are no saved recipes to adapt yet.", true);
                var match = ResolveRecipe(name, recipes);
                if (match is null)
                    return ($"No saved recipe matches \"{name}\". Saved recipes: {string.Join("; ", recipes.Select(r => r.Name))}.", true);
                var adaptResult = await _recipeAdapter.AdaptToOnHandAsync(match.Id, cancellationToken: ct);
                if (adaptResult.Success)
                {
                    actions.Add($"adapted {match.Name}");
                    nav.Url = "/recipes"; // show the new variant; keep listening (not a hand-off)
                }
                return (adaptResult.Message, !adaptResult.Success);
            }

            default:
                return ($"Unknown tool: {call.Name}.", true);
        }
    }

    private static IList<AITool> BuildTools()
    {
        const string categoryEnum = """["Dairy","Meat","Produce","Pantry","Frozen","Beverage","Household","PetCare","PersonalCare","Other"]""";

        // Reuse the existing Anthropic tool definitions, wrapped as AITool via the SDK's AsAITool
        // helper so they flow through IChatClient. Tool calls come back as FunctionCallContent.
        ToolUnion[] tools =
        [
            MakeTool("record_signal",
                "Record an explicit inventory statement about an existing product.",
                $$"""
                {
                  "product_name": { "type": "string", "description": "Canonical product name from the list." },
                  "kind": { "type": "string", "enum": ["OutNow","RunningLow","Restocked"] }
                }
                """,
                ["product_name", "kind"]),

            MakeTool("add_purchase",
                "Log that the user bought a product (feeds the repurchase-interval prediction).",
                """
                {
                  "product_name": { "type": "string", "description": "Canonical product name from the list." },
                  "date": { "type": "string", "description": "ISO 8601 date; omit for today." },
                  "quantity": { "type": "number", "description": "Quantity bought; omit for 1." }
                }
                """,
                ["product_name"]),

            MakeTool("query_status",
                "Report replenishment status. Omit product_name to return the whole running-low list.",
                """
                {
                  "product_name": { "type": "string", "description": "Canonical product name, or omit for the running-low list." }
                }
                """,
                []),

            MakeTool("set_tracking",
                "Start or stop tracking a product for replenishment. tracked=false stops predicting a one-off / unwanted item; tracked=true resumes.",
                """
                {
                  "product_name": { "type": "string", "description": "Canonical product name from the list." },
                  "tracked": { "type": "boolean", "description": "false to stop tracking, true to resume." }
                }
                """,
                ["product_name", "tracked"]),

            MakeTool("create_product",
                "Create a new product. Only when the referenced item has no fuzzy match in the list.",
                $$"""
                {
                  "name": { "type": "string" },
                  "category": { "type": "string", "enum": {{categoryEnum}} },
                  "tags": { "type": "array", "items": { "type": "string" }, "description": "1-3 descriptive tags (e.g. Protein, Snack). Reuse the Known tags list when one fits; coin a new tag only when none does." }
                }
                """,
                ["name", "category"]),

            MakeTool("add_tags",
                "Add descriptive tags to an EXISTING product ('tag the wagyu as beef'). Reuse the Known tags list when one fits.",
                """
                {
                  "product_name": { "type": "string", "description": "Canonical product name from the list." },
                  "tags": { "type": "array", "items": { "type": "string" }, "description": "Tags to add." }
                }
                """,
                ["product_name", "tags"]),

            MakeTool("import_receipts",
                "Scan the configured receipt folder and process any NEW receipt files — depending on the import-mode setting each is recorded directly or queued for review. Use when the user asks to import, upload, scan, or process their receipts.",
                """
                {
                }
                """,
                []),

            MakeTool("open_page",
                "Navigate the user's screen to a page of the app. Use when they ask to see, open, go to, or show a page. For a specific product's detail page use page='product' + product_name. To show the recipes that use a specific product ('what can I make with the chicken', 'recipes using the salmon'), use page='recipes' + product_name.",
                """
                {
                  "page": { "type": "string", "enum": ["dashboard","grocery_list","recipes","trends","upload","receipts","products","accuracy","settings","product"] },
                  "product_name": { "type": "string", "description": "With page='product', the product whose detail page to open. With page='recipes', scope the recipes list to those that use this product. Omit for a whole page." }
                }
                """,
                ["page"]),

            MakeTool("read_recipe",
                "Open a SAVED recipe on screen and read it aloud step-by-step. Use when the user asks to hear, read, or be walked through a recipe — by name, or by position ('read the second recipe') even when no on-screen list is provided.",
                """
                {
                  "recipe_name": { "type": "string", "description": "Name of the saved recipe (close match is fine). Give this OR position." },
                  "position": { "type": "integer", "description": "1-based position in the saved-recipes list, counted the way the Recipes page shows it: newest saved first, adapted variants right after their original. Use when the user refers to a recipe by position rather than name and no on-screen list says otherwise." }
                }
                """),

            MakeTool("suggest_substitutes",
                "Save \"also works as\" substitutes for a product — the recipe ingredients it can stand in for — so recipes recognize what the user has. When the user SAYS what it works as ('add X as a substitute/variant for Y'), pass those phrases in 'substitutes' (saved verbatim on X). Omit 'substitutes' to auto-generate ideas.",
                """
                {
                  "product_name": { "type": "string", "description": "Canonical name of the product the user HAS — the substitute phrases are saved on it." },
                  "substitutes": { "type": "array", "items": { "type": "string" }, "description": "The exact ingredient phrases the user stated it works as (e.g. 'ground beef'). Omit to auto-generate." }
                }
                """,
                ["product_name"]),

            MakeTool("adapt_recipe",
                "Adapt a SAVED recipe to use what the user has on hand — swap the main ingredients they're missing for ones they do have and rewrite the steps/cook times — saving it as a new variant. Use when they ask to adapt, adjust, or remake a recipe with what they have.",
                """
                {
                  "recipe_name": { "type": "string", "description": "Name of the saved recipe to adapt (close match is fine)." }
                }
                """,
                ["recipe_name"]),

            MakeTool("add_recipe_to_list",
                "Generate a recipe for a dish the user names and add the ingredients they DON'T already have to their grocery list. Use when they ask to add the things/ingredients they need for a dish (e.g. \"add everything for steak hibachi\"). It prefers what they own and never includes foods they won't eat. Set include_seasonings=true to also add missing seasonings/spices. After adding the mains you MAY offer to include complementary vegetables (call again with an expanded 'recipe' like \"steak hibachi with vegetables\") or the missing seasonings. If the tool response asks you to confirm before adding, relay the item list to the user and add only after they agree (call again with confirmed=true).",
                """
                {
                  "recipe": { "type": "string", "description": "The dish or recipe request, e.g. \"steak hibachi\" or \"steak hibachi with vegetables\"." },
                  "include_seasonings": { "type": "boolean", "description": "Also add missing seasonings/spices/staples, not just mains. Default false." },
                  "confirmed": { "type": "boolean", "description": "Set true ONLY after the user has agreed to add the proposed items (used when the setting asks to confirm first)." }
                }
                """,
                ["recipe"]),
        ];

        return tools.Select(t => t.AsAITool()).ToList();
    }

    // Exact (case-insensitive) → unique substring either way → token containment (the eval harness's
    // matcher: |q ∩ name| / min sizes ≥ 0.6, unique best) — so "chicken and potatoes" finds
    // "Pan-Seared Chicken with Roasted Potatoes" deterministically. On no match the tool result lists
    // the saved names, so the model can self-correct on its next loop turn.
    private static RecipeRef? ResolveRecipe(string query, IReadOnlyList<RecipeRef> recipes)
    {
        var q = query.Trim();
        var exact = recipes.FirstOrDefault(r => string.Equals(r.Name, q, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        var contains = recipes.Where(r =>
            r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            q.Contains(r.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (contains.Count == 1) return contains[0];

        var qTokens = RecipeTokens(q);
        if (qTokens.Count == 0) return null;
        var scored = recipes
            .Select(r => (Recipe: r, Score: Containment(qTokens, RecipeTokens(r.Name))))
            .Where(x => x.Score >= 0.6)
            .OrderByDescending(x => x.Score)
            .ToList();
        // A unique winner only — two recipes tied above the bar means it's genuinely ambiguous.
        return scored.Count == 1 || (scored.Count > 1 && scored[0].Score > scored[1].Score)
            ? scored[0].Recipe
            : null;
    }

    private static double Containment(HashSet<string> a, HashSet<string> b) =>
        a.Count == 0 || b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / Math.Min(a.Count, b.Count);

    private static HashSet<string> RecipeTokens(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t is not ("the" or "a" or "an" or "and" or "with" or "of" or "recipe"))
            .ToHashSet();

    private static ToolUnion MakeTool(string name, string description, string propertiesJson, string[]? required = null) =>
        new Tool
        {
            Name = name,
            Description = description,
            InputSchema = new InputSchema
            {
                Type = JsonSerializer.SerializeToElement("object"),
                Properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(propertiesJson)!,
                Required = required ?? [],
            },
        };

    private static string ReadEmbedded(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"ShelfAware.Llm.{suffix}")
            ?? throw new InvalidOperationException($"Embedded resource {suffix} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Tool-call arguments arrive as JsonElement (deserialized from the wire) or boxed primitives; read either.
    private static string? AsString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement e => e.ToString(),
        _ => v.ToString(),
    };

    private static decimal? AsDecimal(object? v) => v switch
    {
        decimal d => d,
        double db => (decimal)db,
        int i => i,
        long l => l,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDecimal(),
        string s when decimal.TryParse(s, out var d) => d,
        _ => null,
    };

    private static bool? AsBool(object? v) => v switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        string s when bool.TryParse(s, out var b) => b,
        _ => null,
    };

    private static List<string>? AsStringList(object? v) => v switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Array } e =>
            [.. e.EnumerateArray().Select(x => AsString(x)).OfType<string>()],
        IEnumerable<object?> list => [.. list.Select(AsString).OfType<string>()],
        _ => null,
    };
}
