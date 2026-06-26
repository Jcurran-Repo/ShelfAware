using System.Reflection;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
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
              "confidence":      { "type": "number" },
              "existing_product":{ "type": ["string", "null"], "description": "Exact name from the provided existing-products list this line matches, or null." }
            },
            "required": ["raw_text", "normalized_name", "brand", "quantity", "size", "unit_price", "category", "confidence", "existing_product"],
            "additionalProperties": false
          }
        }
      },
      "required": ["merchant", "purchase_date", "lines"],
      "additionalProperties": false
    }
    """;

    private readonly AnthropicClient _client;
    private readonly LlmOptions _options;

    public AnthropicReceiptExtractor(IOptions<LlmOptions> options)
    {
        _options = options.Value;
        _client = new AnthropicClient { ApiKey = _options.ApiKey };
    }

    public async Task<ExtractionResult> ExtractAsync(
        IReadOnlyList<ReceiptAttachment> attachments,
        IReadOnlyList<string>? knownProductNames = null,
        CancellationToken cancellationToken = default)
    {
        if (attachments.Count == 0) return ExtractionResult.Fail("No attachments provided.");

        var content = new List<ContentBlockParam>();
        foreach (var attachment in attachments)
        {
            content.Add(ToContentBlock(attachment));
        }
        content.Add(new TextBlockParam { Text = "Extract this receipt. All attachments belong to ONE receipt; merge into a single line list." });

        if (knownProductNames is { Count: > 0 })
        {
            content.Add(new TextBlockParam
            {
                Text = "Existing products — set existing_product to the EXACT matching name from this list, or null if none fits:\n- "
                       + string.Join("\n- ", knownProductNames),
            });
        }

        string rawJson = "";
        string? lastError = null;

        // DESIGN.md §5 robustness: one retry with the validation error appended; two failures → friendly error.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var messages = new List<MessageParam> { new() { Role = Role.User, Content = content } };
            if (attempt > 0)
            {
                messages.Add(new MessageParam { Role = Role.Assistant, Content = rawJson });
                messages.Add(new MessageParam
                {
                    Role = Role.User,
                    Content = $"Your previous output failed validation: {lastError}. Output corrected JSON matching the schema.",
                });
            }

            try
            {
                var response = await _client.Messages.Create(new MessageCreateParams
                {
                    Model = _options.ExtractionModel,
                    MaxTokens = 8192,
                    System = SystemPrompt,
                    OutputConfig = new OutputConfig
                    {
                        Format = new JsonOutputFormat
                        {
                            Schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(OutputSchemaJson)!,
                        },
                    },
                    Messages = messages,
                }, cancellationToken: cancellationToken);

                rawJson = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
                var receipt = ParseReceipt(rawJson);
                return ExtractionResult.Ok(receipt, rawJson);
            }
            catch (JsonException ex)
            {
                lastError = ex.Message;
            }
            catch (Exception ex)
            {
                // API/transport errors (auth, rate limit, network) — not fixable by a retry
                // here; the SDK already retries retryable statuses internally.
                return ExtractionResult.Fail(ex.Message, rawJson);
            }
        }

        return ExtractionResult.Fail($"The extraction output could not be parsed after a retry: {lastError}", rawJson);
    }

    private static ContentBlockParam ToContentBlock(ReceiptAttachment attachment)
    {
        var base64 = Convert.ToBase64String(attachment.Data);
        if (attachment.MediaType == "application/pdf")
        {
            return new DocumentBlockParam { Source = new Base64PdfSource { Data = base64 } };
        }

        var mediaType = attachment.MediaType switch
        {
            "image/jpeg" => MediaType.ImageJpeg,
            "image/png" => MediaType.ImagePng,
            "image/gif" => MediaType.ImageGif,
            "image/webp" => MediaType.ImageWebP,
            _ => throw new NotSupportedException($"Unsupported attachment type: {attachment.MediaType}"),
        };
        return new ImageBlockParam { Source = new Base64ImageSource { Data = base64, MediaType = mediaType } };
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
