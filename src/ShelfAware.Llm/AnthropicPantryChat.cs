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
    private readonly ILogger<AnthropicPantryChat> _logger;

    public AnthropicPantryChat(
        IChatClient chat, IOptions<LlmOptions> options, IPantryStore store, ILogger<AnthropicPantryChat> logger,
        IReceiptImporter? importer = null)
    {
        _chat = chat;
        _options = options.Value;
        _store = store;
        _importer = importer;
        _logger = logger;
    }

    public async Task<ChatResult> HandleAsync(
        string userText, IReadOnlyList<ChatTurn>? history = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) return ChatResult.Fail("Type something to update.");

        var products = await _store.GetProductsAsync(cancellationToken);
        var system = SystemPrompt + "\n\nCurrent products:\n" + (products.Count == 0
            ? "(none yet)"
            : string.Join("\n", products.OrderBy(p => p.Name).Select(p => $"- {p.Name} ({p.Category})")));

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
                return ChatResult.Ok(text.Length > 0 ? text : "Done.", actions, nav.Url);
            }

            // Carry the assistant's tool-call turn back into the history, then answer each call.
            messages.AddRange(response.Messages);

            var results = new List<AIContent>();
            foreach (var call in calls)
            {
                var (text, _) = await ExecuteToolAsync(call, products, actions, nav, cancellationToken);
                results.Add(new FunctionResultContent(call.CallId, text));
            }
            messages.Add(new ChatMessage(ChatRole.Tool, results));

            // create_product may have added rows — refresh so later fuzzy matches see them.
            products = await _store.GetProductsAsync(cancellationToken);
        }

        _logger.LogWarning("Pantry chat hit the {MaxTurns}-turn limit without a final reply ({ActionCount} action(s) applied).", MaxTurns, actions.Count);
        return ChatResult.Ok(
            actions.Count > 0 ? $"Applied: {string.Join(", ", actions)}." : "Stopped after several steps without finishing.",
            actions, nav.Url);
    }

    /// <summary>Mutable navigation slot the tool handlers write into (last navigation wins) — the
    /// service is a singleton, so per-request state rides through parameters, never fields.</summary>
    private sealed class NavigationTarget { public string? Url; }

    private async Task<(string text, bool isError)> ExecuteToolAsync(
        FunctionCallContent call, IReadOnlyList<Product> products, List<string> actions, NavigationTarget nav, CancellationToken ct)
    {
        string? Str(string key) => call.Arguments is { } a && a.TryGetValue(key, out var v) ? AsString(v) : null;
        decimal? Dec(string key) => call.Arguments is { } a && a.TryGetValue(key, out var v) ? AsDecimal(v) : null;
        bool? Bool(string key) => call.Arguments is { } a && a.TryGetValue(key, out var v) ? AsBool(v) : null;

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
                await _store.AddPurchaseAsync(product.Id, date, qty, ct);
                actions.Add($"purchase → {product.Name}");
                return ($"Logged {qty:0.##} × {product.Name} on {date:yyyy-MM-dd}.", false);
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
                await _store.CreateProductAsync(name, category, ct);
                actions.Add($"created {name}");
                return ($"Created {name} ({category}).", false);
            }

            case "import_receipts":
            {
                if (_importer is null)
                    return ("Receipt import isn't set up.", true);
                var summary = await _importer.ImportNewAsync(ct);
                if (summary.Imported > 0) actions.Add($"imported {summary.Imported} receipt(s)");
                return (summary.Describe(), false);
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
                string? url = page switch
                {
                    "dashboard" or "home" => "/",
                    "grocery_list" or "list" => "/list",
                    "recipes" => "/recipes",
                    "trends" => "/trends",
                    "upload" or "receipt" => "/receipt",
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
                if (string.IsNullOrWhiteSpace(name)) return ("A recipe name is required.", true);
                var recipes = await _store.GetRecipesAsync(ct);
                if (recipes.Count == 0)
                    return ("There are no saved recipes yet — save one on the Recipes page first.", true);
                var match = ResolveRecipe(name, recipes);
                if (match is null)
                    // Feed the real names back so the model can self-correct or ask which one was meant.
                    return ($"No saved recipe matches \"{name}\". Saved recipes: {string.Join("; ", recipes.Select(r => r.Name))}.", true);
                if (!match.HasSteps)
                    return ($"\"{match.Name}\" has no cooking steps saved, so there's nothing to read aloud.", true);
                nav.Url = $"/recipes?read={match.Id}";
                actions.Add($"reading {match.Name}");
                return ($"Opening {match.Name} and reading it aloud.", false);
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
                  "category": { "type": "string", "enum": {{categoryEnum}} }
                }
                """,
                ["name", "category"]),

            MakeTool("import_receipts",
                "Scan the configured receipt folder and process any NEW receipt files — depending on the import-mode setting each is recorded directly or queued for review. Use when the user asks to import, upload, scan, or process their receipts.",
                """
                {
                }
                """,
                []),

            MakeTool("open_page",
                "Navigate the user's screen to a page of the app. Use when they ask to see, open, go to, or show a page — or a specific product's detail (page='product' + product_name).",
                """
                {
                  "page": { "type": "string", "enum": ["dashboard","grocery_list","recipes","trends","upload","products","accuracy","settings","product"] },
                  "product_name": { "type": "string", "description": "Only with page='product': the product whose detail page to open." }
                }
                """,
                ["page"]),

            MakeTool("read_recipe",
                "Open a SAVED recipe on screen and read it aloud step-by-step. Use when the user asks to hear, read, or be walked through a recipe.",
                """
                {
                  "recipe_name": { "type": "string", "description": "Name of the saved recipe (close match is fine)." }
                }
                """,
                ["recipe_name"]),
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

    private static ToolUnion MakeTool(string name, string description, string propertiesJson, string[] required) =>
        new Tool
        {
            Name = name,
            Description = description,
            InputSchema = new InputSchema
            {
                Type = JsonSerializer.SerializeToElement("object"),
                Properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(propertiesJson)!,
                Required = required,
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
}
