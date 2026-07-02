using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;

namespace ShelfAware.Llm.Tests;

public class ReceiptExtractorTests
{
    private static AnthropicReceiptExtractor Extractor(FakeChatClient client) =>
        new(client, Options.Create(new LlmOptions()), NullLogger<AnthropicReceiptExtractor>.Instance);

    private static readonly IReadOnlyList<ReceiptAttachment> OneImage = [new([1, 2, 3], "image/jpeg")];

    private const string ValidJson = """
    {
      "merchant": "Walmart",
      "purchase_date": "2026-06-15",
      "lines": [
        {
          "raw_text": "GV WHL MLK 1GAL", "normalized_name": "Whole Milk", "brand": "Great Value",
          "quantity": 1, "size": "1 gal", "unit_price": 3.24, "category": "Dairy",
          "tags": ["Dairy"], "confidence": 0.95, "existing_product": null
        }
      ]
    }
    """;

    [Fact]
    public async Task Parses_a_valid_extraction()
    {
        var result = await Extractor(FakeChatClient.Returning(Responses.Text(ValidJson))).ExtractAsync(OneImage);

        Assert.True(result.Success);
        Assert.Equal("Walmart", result.Receipt!.Merchant);
        Assert.Equal(new DateOnly(2026, 6, 15), result.Receipt.PurchaseDate);
        var line = Assert.Single(result.Receipt.Lines);
        Assert.Equal("Whole Milk", line.NormalizedName);
        Assert.Equal("Great Value", line.Brand);
        Assert.Equal("1 gal", line.Size);
        Assert.Equal(Category.Dairy, line.Category);
    }

    [Fact]
    public async Task Retries_once_on_unparseable_output_then_succeeds()
    {
        var client = new FakeChatClient(
            () => Responses.Text("not json at all"),
            () => Responses.Text(ValidJson));

        var result = await Extractor(client).ExtractAsync(OneImage);

        Assert.True(result.Success);
        Assert.Equal(2, client.CallCount); // one retry happened
    }

    [Fact]
    public async Task Fails_after_two_unparseable_outputs()
    {
        var client = new FakeChatClient(
            () => Responses.Text("nope"),
            () => Responses.Text("still nope"));

        var result = await Extractor(client).ExtractAsync(OneImage);

        Assert.False(result.Success);
        Assert.Equal(2, client.CallCount); // one retry, then give up
    }

    [Fact]
    public async Task A_model_failure_is_reported_without_retrying()
    {
        var client = new FakeChatClient(() => throw new HttpRequestException("down"));

        var result = await Extractor(client).ExtractAsync(OneImage);

        Assert.False(result.Success);
        Assert.Equal(1, client.CallCount); // transport errors aren't retried here
    }

    [Fact]
    public async Task No_attachments_fails_before_any_call()
    {
        var client = FakeChatClient.Returning(Responses.Text(ValidJson));

        var result = await Extractor(client).ExtractAsync([]);

        Assert.False(result.Success);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Clamps_confidence_defaults_unknown_category_and_dedupes_tags()
    {
        const string json = """
        {
          "merchant": null, "purchase_date": null,
          "lines": [
            {
              "raw_text": "x", "normalized_name": "Thing", "brand": null, "quantity": 1, "size": null,
              "unit_price": null, "category": "Nonsense", "tags": ["Snack", "snack", "Canned"],
              "confidence": 1.7, "existing_product": null
            }
          ]
        }
        """;

        var result = await Extractor(FakeChatClient.Returning(Responses.Text(json))).ExtractAsync(OneImage);

        var line = Assert.Single(result.Receipt!.Lines);
        Assert.Equal(1m, line.Confidence);            // clamped from 1.7
        Assert.Equal(Category.Other, line.Category);  // "Nonsense" → Other
        Assert.Equal(2, line.Tags.Length);            // "Snack"/"snack" deduped
    }

    [Fact]
    public async Task Carries_the_existing_product_suggestion_through()
    {
        const string json = """
        {
          "merchant": null, "purchase_date": null,
          "lines": [
            {
              "raw_text": "x", "normalized_name": "Milk", "brand": null, "quantity": 1, "size": null,
              "unit_price": null, "category": "Dairy", "tags": [], "confidence": 0.9,
              "existing_product": "Whole Milk"
            }
          ]
        }
        """;

        var result = await Extractor(FakeChatClient.Returning(Responses.Text(json))).ExtractAsync(OneImage);

        Assert.Equal("Whole Milk", result.Receipt!.Lines[0].SuggestedProductName);
    }
}
