using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Extraction;
using Category = ShelfAware.Core.Domain.Category;

namespace ShelfAware.Llm;

public class AnthropicReceiptExtractor : IReceiptExtractor
{
    private static readonly string SystemPrompt = ReadEmbedded("Prompts.receipt-extraction-system.txt");

    // Mirrors DESIGN.md §5 output contract. Structured-outputs strict mode:
    // every property required (nullables via type unions), additionalProperties false,
    // no numeric range constraints (unsupported) — confidence is clamped in code.
    private const string OutputSchemaJson = """
    {
      "type": "object",
      "properties": {
        "merchant": { "type": ["string", "null"] },
        "purchase_date": { "type": ["string", "null"], "description": "ISO 8601 date" },
        "lines": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "raw_text":        { "type": "string" },
              "normalized_name": { "type": "string" },
              "brand":           { "type": ["string", "null"] },
              "quantity":        { "type": "number" },
              "size":            { "type": ["string", "null"], "description": "e.g. '1 gal', '12 ct'" },
              "unit_price":      { "type": ["number", "null"] },
              "category":        { "type": "string", "enum": ["Dairy","Meat","Produce","Pantry","Frozen","Beverage","Household","PetCare","PersonalCare","Other"] },
              "tags":            { "type": "array", "items": { "type": "string" }, "description": "Descriptive tags, additional to category. [] when none apply." },
              "confidence":      { "type": "number" },
              "existing_product":{ "type": ["string", "null"], "description": "Exact name from the provided existing-products list this line matches, or null." }
            },
            "required": ["raw_text", "normalized_name", "brand", "quantity", "size", "unit_price", "category", "tags", "confidence", "existing_product"],
            "additionalProperties": false
          }
        }
      },
      "required": ["merchant", "purchase_date", "lines"],
      "additionalProperties": false
    }
    """;

    private readonly IChatClient _chat;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicReceiptExtractor> _logger;

    public AnthropicReceiptExtractor(IChatClient chat, IOptions<LlmOptions> options, ILogger<AnthropicReceiptExtractor> logger)
    {
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ExtractionResult> ExtractAsync(
        IReadOnlyList<ReceiptAttachment> attachments,
        IReadOnlyList<string>? knownProductNames = null,
        IReadOnlyList<string>? knownTags = null,
        CancellationToken cancellationToken = default)
    {
        if (attachments.Count == 0) return ExtractionResult.Fail("No attachments provided.");

        _logger.LogInformation("Extracting receipt from {AttachmentCount} attachment(s) ({ProductHints} product hints, {TagHints} tag hints).",
            attachments.Count, knownProductNames?.Count ?? 0, knownTags?.Count ?? 0);

        var content = new List<AIContent>();
        foreach (var attachment in attachments)
        {
            content.Add(new DataContent(attachment.Data, attachment.MediaType));
        }
        content.Add(new TextContent("Extract this receipt. All attachments belong to ONE receipt; merge into a single line list."));

        if (knownProductNames is { Count: > 0 })
        {
            content.Add(new TextContent(
                "Existing products — set existing_product to the EXACT matching name from this list, or null if none fits:\n- "
                + string.Join("\n- ", knownProductNames)));
        }

        if (knownTags is { Count: > 0 })
        {
            content.Add(new TextContent(
                "Existing tags — when a tag fits, REUSE one from this list verbatim instead of coining a near-duplicate; only invent a new tag if none fit:\n- "
                + string.Join("\n- ", knownTags)));
        }

        string rawJson = "";
        string? lastError = null;

        var options = new ChatOptions
        {
            ModelId = _options.ExtractionModel,
            MaxOutputTokens = 8192,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                JsonSerializer.Deserialize<JsonElement>(OutputSchemaJson),
                schemaName: "receipt_extraction"),
        };

        // DESIGN.md §5 robustness: one retry with the validation error appended; two failures → friendly error.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, content),
            };
            if (attempt > 0)
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, rawJson));
                messages.Add(new ChatMessage(ChatRole.User,
                    $"Your previous output failed validation: {lastError}. Output corrected JSON matching the schema."));
            }

            try
            {
                var response = await _chat.GetResponseAsync(messages, options, cancellationToken);

                rawJson = response.Text;
                var receipt = ParseReceipt(rawJson);
                _logger.LogInformation("Extraction succeeded: {LineCount} line(s), merchant {Merchant}.",
                    receipt.Lines.Count, receipt.Merchant ?? "(none)");
                return ExtractionResult.Ok(receipt, rawJson);
            }
            catch (JsonException ex)
            {
                lastError = ex.Message;
                _logger.LogWarning("Extraction attempt {Attempt} produced unparseable output: {Error}", attempt + 1, ex.Message);
            }
            catch (Exception ex)
            {
                // API/transport errors (auth, rate limit, network) — not fixable by a retry
                // here; the SDK already retries retryable statuses internally.
                _logger.LogError(ex, "Extraction call to the model failed.");
                return ExtractionResult.Fail(ex.Message, rawJson);
            }
        }

        _logger.LogWarning("Extraction failed after a retry: {Error}", lastError);
        return ExtractionResult.Fail($"The extraction output could not be parsed after a retry: {lastError}", rawJson);
    }

    private static ExtractedReceipt ParseReceipt(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var lines = new List<ExtractedLine>();
        foreach (var line in root.GetProperty("lines").EnumerateArray())
        {
            lines.Add(new ExtractedLine
            {
                RawText = line.GetProperty("raw_text").GetString() ?? "",
                NormalizedName = line.GetProperty("normalized_name").GetString() ?? "",
                Brand = GetNullableString(line, "brand"),
                Quantity = line.GetProperty("quantity").GetDecimal(),
                Size = GetNullableString(line, "size"),
                UnitPrice = line.TryGetProperty("unit_price", out var up) && up.ValueKind == JsonValueKind.Number ? up.GetDecimal() : null,
                Category = Enum.TryParse<Category>(line.GetProperty("category").GetString(), ignoreCase: true, out var cat) ? cat : Category.Other,
                Tags = ParseTags(line),
                Confidence = Math.Clamp(line.GetProperty("confidence").GetDecimal(), 0m, 1m),
                SuggestedProductName = GetNullableString(line, "existing_product"),
            });
        }

        return new ExtractedReceipt
        {
            Merchant = GetNullableString(root, "merchant"),
            PurchaseDate = DateOnly.TryParse(GetNullableString(root, "purchase_date"), out var d) ? d : null,
            Lines = lines,
        };
    }

    private static string[] ParseTags(JsonElement line) =>
        line.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

    private static string? GetNullableString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static string ReadEmbedded(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"ShelfAware.Llm.{suffix}")
            ?? throw new InvalidOperationException($"Embedded resource {suffix} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
