using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Census;
using ShelfAware.Core.Domain;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// The shelf-photo reader (DESIGN.md §13.8). Most of these pin the parse's ENFORCEMENT rather than its
/// deserialization: a receipt's output can be checked against printed text and a shelf photo's cannot, so
/// the contract's honesty rules are held in code instead of hoped for from the prompt.
/// </summary>
public class ShelfCensusReaderTests
{
    private static AnthropicShelfCensusReader Reader(FakeChatClient client) =>
        new(client, Options.Create(new LlmOptions()), NullLogger<AnthropicShelfCensusReader>.Instance);

    private static readonly IReadOnlyList<ShelfPhoto> OnePhoto = [new([1, 2, 3], "image/jpeg")];

    private static string Json(string items) => $$"""{ "items": [ {{items}} ] }""";

    private const string TilapiaItem = """
    {
      "label_text": "TILAPIA FILLETS 16 OZ", "evidence": "Label", "normalized_name": "Tilapia Fillets",
      "brand": "Great Value", "size": "16 oz", "variety": null, "category": "Frozen",
      "visible_count": 3, "confidence": 0.92, "existing_product": null
    }
    """;

    [Fact]
    public async Task Parses_a_valid_census()
    {
        var result = await Reader(FakeChatClient.Returning(Responses.Text(Json(TilapiaItem)))).ReadAsync(OnePhoto);

        Assert.True(result.Success);
        var item = Assert.Single(result.Items);
        Assert.Equal("TILAPIA FILLETS 16 OZ", item.LabelText);
        Assert.Equal(CensusEvidence.Label, item.Evidence);
        Assert.Equal("Tilapia Fillets", item.NormalizedName);
        Assert.Equal("Great Value", item.Brand);
        Assert.Equal("16 oz", item.Size);
        Assert.Equal(Category.Frozen, item.Category);
        Assert.Equal(3, item.VisibleCount);
        Assert.Equal(0.92m, item.Confidence);
    }

    [Fact]
    public async Task No_photos_is_refused_without_calling_the_model()
    {
        var client = FakeChatClient.Returning(Responses.Text(Json(TilapiaItem)));

        var result = await Reader(client).ReadAsync([]);

        Assert.False(result.Success);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task A_photo_of_something_that_is_not_a_shelf_reads_as_empty_not_as_a_failure()
    {
        // The same convention receipt extraction uses for a non-receipt image. "I looked and there was no
        // food here" is a successful read with nothing in it, and the page can say so plainly.
        var result = await Reader(FakeChatClient.Returning(Responses.Text("""{ "items": [] }"""))).ReadAsync(OnePhoto);

        Assert.True(result.Success);
        Assert.Empty(result.Items);
    }

    // ---- The honesty rules ----

    [Fact]
    public async Task A_Label_claim_with_no_readable_text_is_downgraded_to_Appearance()
    {
        // The whole value of the Label grade is that a human can check it against the photo in a second.
        // With label_text null there is nothing to check, so the claim is recorded as what it actually is.
        var result = await Reader(FakeChatClient.Returning(Responses.Text(Json("""
        {
          "label_text": null, "evidence": "Label", "normalized_name": "Chicken Breast",
          "brand": null, "size": null, "variety": null, "category": "Frozen",
          "visible_count": 1, "confidence": 0.8, "existing_product": null
        }
        """)))).ReadAsync(OnePhoto);

        var item = Assert.Single(result.Items);
        Assert.Equal(CensusEvidence.Appearance, item.Evidence);
        Assert.Null(item.LabelText);
    }

    [Fact]
    public async Task Whitespace_only_label_text_counts_as_no_label()
    {
        var result = await Reader(FakeChatClient.Returning(Responses.Text(Json("""
        {
          "label_text": "   ", "evidence": "Label", "normalized_name": "Chicken Breast",
          "brand": null, "size": null, "variety": null, "category": "Frozen",
          "visible_count": 1, "confidence": 0.8, "existing_product": null
        }
        """)))).ReadAsync(OnePhoto);

        var item = Assert.Single(result.Items);
        Assert.Null(item.LabelText);
        Assert.Equal(CensusEvidence.Appearance, item.Evidence);
    }

    [Fact]
    public async Task An_unidentified_package_cannot_claim_to_be_confident()
    {
        // Confidence means certainty in the IDENTIFICATION, and this item declined to identify anything.
        // Left ungoverned, a confident "foil-wrapped parcel" would be TICKED by default in the review grid
        // — the grid reads one number, so the two fields must not be able to tell different stories.
        var result = await Reader(FakeChatClient.Returning(Responses.Text(Json("""
        {
          "label_text": null, "evidence": "Unidentified", "normalized_name": "foil-wrapped parcel",
          "brand": null, "size": null, "variety": null, "category": "Other",
          "visible_count": 4, "confidence": 0.95, "existing_product": null
        }
        """)))).ReadAsync(OnePhoto);

        var item = Assert.Single(result.Items);
        Assert.Equal(CensusEvidence.Unidentified, item.Evidence);
        Assert.Equal(AnthropicShelfCensusReader.MaxUnidentifiedConfidence, item.Confidence);
        Assert.True(item.Confidence < 0.6m, "an unidentified package must land below the review grid's tick threshold");
    }

    [Fact]
    public async Task An_unidentified_package_is_never_matched_to_an_existing_product()
    {
        // Its name describes a CONTAINER. Matching it would attach a count to a real product on no
        // evidence whatsoever — the one move that turns "I can't tell" into a confident lie.
        var result = await Reader(FakeChatClient.Returning(Responses.Text(Json("""
        {
          "label_text": null, "evidence": "Unidentified", "normalized_name": "unlabeled plastic tub",
          "brand": null, "size": null, "variety": null, "category": "Other",
          "visible_count": 1, "confidence": 0.2, "existing_product": "Chicken Breast"
        }
        """)))).ReadAsync(OnePhoto, ["Chicken Breast"]);

        Assert.Null(Assert.Single(result.Items).SuggestedProductName);
    }

    [Fact]
    public async Task An_unrecognised_evidence_value_falls_back_to_Appearance_not_Unidentified()
    {
        // Both fallbacks claim less than Label, but only this one keeps the NAME meaning what it says.
        // Unidentified would redefine "Chicken Breast" as a description of the packaging.
        var result = await Reader(FakeChatClient.Returning(Responses.Text(Json("""
        {
          "label_text": null, "evidence": "PureGuesswork", "normalized_name": "Chicken Breast",
          "brand": null, "size": null, "variety": null, "category": "Frozen",
          "visible_count": 1, "confidence": 0.5, "existing_product": null
        }
        """)))).ReadAsync(OnePhoto);

        var item = Assert.Single(result.Items);
        Assert.Equal(CensusEvidence.Appearance, item.Evidence);
        Assert.Equal("Chicken Breast", item.NormalizedName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task A_visible_count_below_one_is_floored(int reported)
    {
        // Reporting an item means something was SEEN, so zero is incoherent — and a zero that reached the
        // review grid could be confirmed into an ATTESTED zero, which writes a real OutNow into the cadence
        // engine (§13.4). A machine's arithmetic must never mint one.
        var result = await Reader(FakeChatClient.Returning(Responses.Text(Json($$"""
        {
          "label_text": "BEANS", "evidence": "Label", "normalized_name": "Black Beans",
          "brand": null, "size": null, "variety": null, "category": "Pantry",
          "visible_count": {{reported}}, "confidence": 0.9, "existing_product": null
        }
        """)))).ReadAsync(OnePhoto);

        Assert.Equal(1, Assert.Single(result.Items).VisibleCount);
    }

    [Theory]
    [InlineData(1.7, 1.0)]
    [InlineData(-0.5, 0.0)]
    public async Task Confidence_is_clamped_to_the_unit_range(double reported, double expected)
    {
        var result = await Reader(FakeChatClient.Returning(Responses.Text(Json($$"""
        {
          "label_text": "BEANS", "evidence": "Label", "normalized_name": "Black Beans",
          "brand": null, "size": null, "variety": null, "category": "Pantry",
          "visible_count": 2, "confidence": {{reported}}, "existing_product": null
        }
        """)))).ReadAsync(OnePhoto);

        Assert.Equal((decimal)expected, Assert.Single(result.Items).Confidence);
    }

    // ---- Robustness, the §5 contract ----

    [Fact]
    public async Task Retries_once_on_unparseable_output_then_succeeds()
    {
        var client = new FakeChatClient(
            () => Responses.Text("not json at all"),
            () => Responses.Text(Json(TilapiaItem)));

        var result = await Reader(client).ReadAsync(OnePhoto);

        Assert.True(result.Success);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task Retries_on_parseable_but_wrong_shape_output()
    {
        var client = new FakeChatClient(
            () => Responses.Text("""{ "shelves": [] }"""),
            () => Responses.Text(Json(TilapiaItem)));

        var result = await Reader(client).ReadAsync(OnePhoto);

        Assert.True(result.Success);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task Fails_after_two_unparseable_outputs()
    {
        var client = new FakeChatClient(
            () => Responses.Text("nope"),
            () => Responses.Text("still nope"));

        var result = await Reader(client).ReadAsync(OnePhoto);

        Assert.False(result.Success);
        Assert.Equal(2, client.CallCount);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task An_api_failure_is_not_retried()
    {
        // The SDK already retried whatever is retryable; a second attempt here only costs the visitor
        // another call against their own key.
        var client = new FakeChatClient(() => throw new HttpRequestException("401 unauthorized"));

        var result = await Reader(client).ReadAsync(OnePhoto);

        Assert.False(result.Success);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task A_cancellation_propagates_rather_than_reading_as_a_failed_photo()
    {
        var client = new FakeChatClient(() => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Reader(client).ReadAsync(OnePhoto));
    }

    // ---- What the model is actually sent ----

    [Fact]
    public async Task Every_photo_and_the_product_list_reach_the_model()
    {
        var client = FakeChatClient.Returning(Responses.Text(Json(TilapiaItem)));

        await Reader(client).ReadAsync(
            [new([1], "image/jpeg"), new([2], "image/jpeg"), new([3], "image/jpeg")],
            ["Tilapia Fillets", "Black Beans"]);

        var user = Assert.Single(client.ReceivedMessages).Last(m => m.Role == ChatRole.User);
        Assert.Equal(3, user.Contents.OfType<DataContent>().Count());
        var text = string.Join("\n", user.Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains("Tilapia Fillets", text);
        Assert.Contains("Black Beans", text);
    }

    [Fact]
    public async Task With_no_product_list_the_model_is_not_asked_to_match()
    {
        var client = FakeChatClient.Returning(Responses.Text(Json(TilapiaItem)));

        await Reader(client).ReadAsync(OnePhoto);

        var user = Assert.Single(client.ReceivedMessages).Last(m => m.Role == ChatRole.User);
        var text = string.Join("\n", user.Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.DoesNotContain("Existing products", text);
    }
}
