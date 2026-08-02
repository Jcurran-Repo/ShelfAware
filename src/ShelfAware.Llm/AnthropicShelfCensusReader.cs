using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Census;
using Category = ShelfAware.Core.Domain.Category;

namespace ShelfAware.Llm;

/// <summary>
/// Reads a shelf photo into candidate items (DESIGN.md §13.8). Same shape as
/// <see cref="AnthropicReceiptExtractor"/> — vision model, strict output schema, validate-and-retry-once —
/// with the differences a shelf forces: no merchant, no date, no prices, a count of what is VISIBLE, and an
/// evidence grade on every item.
/// <para>The parse deliberately does more than deserialize. A receipt's output is checkable against printed
/// text; a shelf photo's is not, so three of the contract's honesty rules are ENFORCED here rather than
/// trusted to the prompt — see <see cref="ParseItem"/>. A model that drifts off the prompt then produces a
/// weaker claim, never a stronger one.</para>
/// </summary>
public class AnthropicShelfCensusReader : IShelfCensusReader
{
    private static readonly string SystemPrompt = ReadEmbedded("Prompts.shelf-census-system.txt");

    /// <summary>The most an <see cref="CensusEvidence.Unidentified"/> item is allowed to claim (the prompt's
    /// own ceiling, enforced). Confidence means certainty in the IDENTIFICATION, and an item the reader
    /// declined to identify has none — so a high number there would tick "foil-wrapped parcel" into a
    /// household's counts by default. Keeping the two fields consistent here leaves the grid one rule to
    /// read (confidence) instead of two that can disagree.</summary>
    internal const decimal MaxUnidentifiedConfidence = 0.3m;

    // Structured-outputs strict mode, same constraints as the receipt schema: every property required
    // (nullables via type unions), additionalProperties false, no numeric ranges (clamped in code).
    private const string OutputSchemaJson = """
    {
      "type": "object",
      "properties": {
        "items": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "label_text":      { "type": ["string", "null"], "description": "Text legible on the package, VERBATIM. Null when nothing could be read." },
              "evidence":        { "type": "string", "enum": ["Label", "Appearance", "Unidentified"], "description": "How the item was identified. Unidentified = a visible package that could not be named." },
              "normalized_name": { "type": "string", "description": "Canonical item name, brand/size/variety stripped. For Unidentified items, describes the PACKAGE." },
              "brand":           { "type": ["string", "null"] },
              "size":            { "type": ["string", "null"], "description": "e.g. '16 oz', '1 gal'" },
              "variety":         { "type": ["string", "null"], "description": "Flavor/varietal, e.g. 'Strawberry', 'Gala'. Null when the item has none." },
              "category":        { "type": "string", "enum": ["Dairy","Meat","Produce","Pantry","Frozen","Beverage","Household","PetCare","PersonalCare","Other"] },
              "visible_count":   { "type": "integer", "description": "How many of this item are VISIBLE. Never extrapolated for stacking or occlusion." },
              "confidence":      { "type": "number", "description": "Certainty in the identification, 0-1." },
              "existing_product":{ "type": ["string", "null"], "description": "Exact name from the provided existing-products list this item matches, or null." }
            },
            "required": ["label_text", "evidence", "normalized_name", "brand", "size", "variety", "category", "visible_count", "confidence", "existing_product"],
            "additionalProperties": false
          }
        }
      },
      "required": ["items"],
      "additionalProperties": false
    }
    """;

    private readonly IChatClient _chat;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicShelfCensusReader> _logger;

    public AnthropicShelfCensusReader(IChatClient chat, IOptions<LlmOptions> options, ILogger<AnthropicShelfCensusReader> logger)
    {
        _chat = chat;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ShelfCensusResult> ReadAsync(
        IReadOnlyList<ShelfPhoto> photos,
        IReadOnlyList<string>? knownProductNames = null,
        CancellationToken cancellationToken = default)
    {
        if (photos.Count == 0) return ShelfCensusResult.Fail("No photos provided.");

        _logger.LogInformation("Reading a shelf census from {PhotoCount} photo(s) ({ProductHints} product hints).",
            photos.Count, knownProductNames?.Count ?? 0);

        var content = new List<AIContent>();
        foreach (var photo in photos)
        {
            content.Add(new DataContent(photo.Data, photo.MediaType));
        }
        content.Add(new TextContent(
            "Take stock of what is visible in these photos. They show different parts of ONE storage space; "
            + "merge them into a single item list. Report only what you can actually see."));

        if (knownProductNames is { Count: > 0 })
        {
            content.Add(new TextContent(
                "Existing products — set existing_product to the EXACT matching name from this list, or null if none fits:\n- "
                + string.Join("\n- ", knownProductNames)));
        }

        string rawJson = "";
        string? lastError = null;

        var options = new ChatOptions
        {
            ModelId = _options.ExtractionModel,
            MaxOutputTokens = 8192,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                JsonSerializer.Deserialize<JsonElement>(OutputSchemaJson),
                schemaName: "shelf_census"),
        };

        // Same robustness contract as §5 extraction: one retry with the validation error appended.
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

            ChatResponse response;
            try
            {
                response = await _chat.GetResponseAsync(messages, options, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // the caller cancelled — not a read failure
            }
            catch (Exception ex)
            {
                // API/transport errors (auth, rate limit, network) — the SDK already retried what's
                // retryable, so a second attempt here would only cost the visitor another call.
                _logger.LogError(ex, "Shelf census call to the model failed.");
                return ShelfCensusResult.Fail(ex.Message, rawJson);
            }

            rawJson = response.Text;
            try
            {
                var items = ParseItems(rawJson);
                _logger.LogInformation(
                    "Shelf census read {ItemCount} item(s): {Labelled} off labels, {Seen} by appearance, {Unknown} unidentified.",
                    items.Count,
                    items.Count(i => i.Evidence == CensusEvidence.Label),
                    items.Count(i => i.Evidence == CensusEvidence.Appearance),
                    items.Count(i => i.Evidence == CensusEvidence.Unidentified));
                return ShelfCensusResult.Ok(items, rawJson);
            }
            catch (Exception ex)
            {
                // Parseable-but-wrong-shape output throws from the property reads and is retryable the
                // same way malformed JSON is — the §5 contract, applied here.
                lastError = ex.Message;
                _logger.LogWarning("Shelf census attempt {Attempt} produced invalid output: {Error}", attempt + 1, ex.Message);
            }
        }

        _logger.LogWarning("Shelf census failed after a retry: {Error}", lastError);
        return ShelfCensusResult.Fail($"The photo couldn't be read after a retry: {lastError}", rawJson);
    }

    private static List<CensusItem> ParseItems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<CensusItem>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            items.Add(ParseItem(item));
        }
        return items;
    }

    private static CensusItem ParseItem(JsonElement item)
    {
        var labelText = GetNullableString(item, "label_text") is { } text && text.Trim().Length > 0
            ? text.Trim()
            : null;

        // An unparseable evidence value falls back to Appearance, NOT Unidentified: we still hold a name
        // that describes a food, and Unidentified would redefine that name as describing a package. Both
        // fallbacks claim less than Label; only this one keeps the name meaning what it says.
        var evidence = Enum.TryParse<CensusEvidence>(item.GetProperty("evidence").GetString(), ignoreCase: true, out var e)
            ? e
            : CensusEvidence.Appearance;

        // A "Label" claim with no readable text isn't one. The whole value of the grade is that a human can
        // check it against the photo in a second, and there's nothing to check here — so the claim is
        // downgraded to what it actually is rather than left asserting a label nobody can see.
        if (evidence == CensusEvidence.Label && labelText is null) evidence = CensusEvidence.Appearance;

        var confidence = Math.Clamp(item.GetProperty("confidence").GetDecimal(), 0m, 1m);
        if (evidence == CensusEvidence.Unidentified) confidence = Math.Min(confidence, MaxUnidentifiedConfidence);

        return new CensusItem
        {
            LabelText = labelText,
            Evidence = evidence,
            NormalizedName = item.GetProperty("normalized_name").GetString() ?? "",
            Brand = GetNullableString(item, "brand"),
            Size = GetNullableString(item, "size"),
            Variety = GetNullableString(item, "variety"),
            Category = Enum.TryParse<Category>(item.GetProperty("category").GetString(), ignoreCase: true, out var cat)
                ? cat
                : Category.Other,
            // Floored at 1, and this is load-bearing rather than tidiness. Reporting an item at all means
            // something was seen, so 0 is incoherent — and a zero that survived to the review grid could be
            // confirmed into an ATTESTED zero, which writes a real OutNow into the cadence engine (§13.4).
            // A machine's arithmetic must never mint one; a human typing 0 in the grid still can.
            VisibleCount = Math.Max(1, item.GetProperty("visible_count").GetInt32()),
            Confidence = confidence,
            // An Unidentified item names a package, so it can't be "the same item" as anything in the
            // catalog — a match here would attach a count to a product on no evidence at all.
            SuggestedProductName = evidence == CensusEvidence.Unidentified
                ? null
                : GetNullableString(item, "existing_product"),
        };
    }

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
