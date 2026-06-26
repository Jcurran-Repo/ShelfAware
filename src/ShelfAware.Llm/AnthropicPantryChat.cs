using System.Reflection;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
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

    private readonly AnthropicClient _client;
    private readonly LlmOptions _options;
    private readonly IPantryStore _store;

    public AnthropicPantryChat(IOptions<LlmOptions> options, IPantryStore store)
    {
        _options = options.Value;
        _store = store;
        _client = new AnthropicClient { ApiKey = _options.ApiKey };
    }

    public async Task<ChatResult> HandleAsync(string userText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userText)) return ChatResult.Fail("Type something to update.");

        var products = await _store.GetProductsAsync(cancellationToken);
        var system = SystemPrompt + "\n\nCurrent products:\n" + (products.Count == 0
            ? "(none yet)"
            : string.Join("\n", products.OrderBy(p => p.Name).Select(p => $"- {p.Name} ({p.Category})")));

        var tools = BuildTools();
        var messages = new List<MessageParam> { new() { Role = Role.User, Content = userText } };
        var actions = new List<string>();

        for (var turn = 0; turn < MaxTurns; turn++)
        {
            Message response;
            try
            {
                response = await _client.Messages.Create(new MessageCreateParams
                {
                    Model = _options.ChatModel,
                    MaxTokens = 1024,
                    System = system,
                    Tools = tools,
                    Messages = messages,
                }, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                return ChatResult.Fail($"Sorry — I couldn't reach the assistant just now. ({ex.Message})");
            }

            var toolUses = response.Content.Select(b => b.Value).OfType<ToolUseBlock>().ToList();
            if (toolUses.Count == 0)
            {
                var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
                return ChatResult.Ok(text.Length > 0 ? text : "Done.", actions);
            }

            // Echo the assistant turn (it must carry the tool_use blocks), then answer with results.
            var assistantContent = new List<ContentBlockParam>();
            foreach (var block in response.Content)
            {
                switch (block.Value)
                {
                    case TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text):
                        assistantContent.Add(new TextBlockParam { Text = tb.Text });
                        break;
                    case ToolUseBlock tu:
                        assistantContent.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                        break;
                }
            }
            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });

            var resultContent = new List<ContentBlockParam>();
            foreach (var tu in toolUses)
            {
                var (text, isError) = await ExecuteToolAsync(tu, products, actions, cancellationToken);
                resultContent.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = text, IsError = isError });
            }
            messages.Add(new MessageParam { Role = Role.User, Content = resultContent });

            // create_product may have added rows — refresh so later fuzzy matches see them.
            products = await _store.GetProductsAsync(cancellationToken);
        }

        return ChatResult.Ok(
            actions.Count > 0 ? $"Applied: {string.Join(", ", actions)}." : "Stopped after several steps without finishing.",
            actions);
    }

    private async Task<(string text, bool isError)> ExecuteToolAsync(
        ToolUseBlock tool, IReadOnlyList<Product> products, List<string> actions, CancellationToken ct)
    {
        string? Str(string key) =>
            tool.Input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        decimal? Dec(string key) =>
            tool.Input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

        switch (tool.Name)
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

            default:
                return ($"Unknown tool: {tool.Name}.", true);
        }
    }

    private static IReadOnlyList<ToolUnion> BuildTools()
    {
        const string categoryEnum = """["Dairy","Meat","Produce","Pantry","Frozen","Beverage","Household","PetCare","PersonalCare","Other"]""";

        return
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

            MakeTool("create_product",
                "Create a new product. Only when the referenced item has no fuzzy match in the list.",
                $$"""
                {
                  "name": { "type": "string" },
                  "category": { "type": "string", "enum": {{categoryEnum}} }
                }
                """,
                ["name", "category"]),
        ];
    }

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
}
