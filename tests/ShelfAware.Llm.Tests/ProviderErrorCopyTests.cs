using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Census;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Speech;

namespace ShelfAware.Llm.Tests;

/// <summary>
/// ⚠️ A provider's exception text must never reach the screen.
///
/// Every service at this boundary catches, logs the exception, and returns a failure carrying a message
/// the page renders verbatim. Until 2026-09-19 that message WAS <c>ex.Message</c> at six sites, so an
/// <c>HttpRequestException</c> string was what someone holding a receipt read. Two problems: it can name
/// an internal host, a path or a provider account detail, and it reads as a crash rather than as
/// something to try again.
///
/// These tests pin the rule by its observable consequence rather than by the exact copy — they assert the
/// distinctive text of the thrown exception is absent, and that a non-empty message is still returned. So
/// the wording stays free to change and the leak cannot come back. The detail is not lost: every one of
/// these logs at Error, which the error-log pipeline captures and renders on /admin.
/// </summary>
public class ProviderErrorCopyTests
{
    // Distinctive enough that its presence in a result can only mean the exception text was passed
    // through — and shaped like the thing actually worth hiding.
    private const string Secret = "https://internal-gateway.corp.local:8443 unauthorized for key sk-ant-XYZ";

    private static void AssertSafe(string? error)
    {
        Assert.False(string.IsNullOrWhiteSpace(error), "A failure must still say something to the person.");
        Assert.DoesNotContain("internal-gateway", error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-ant", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Receipt extraction --------------------------------------------------------------------

    private static AnthropicReceiptExtractor Extractor(FakeChatClient client) =>
        new(client, Options.Create(new LlmOptions()), NullLogger<AnthropicReceiptExtractor>.Instance);

    private static readonly IReadOnlyList<ReceiptAttachment> OneImage = [new([1, 2, 3], "image/jpeg")];

    [Fact]
    public async Task A_failed_extraction_call_does_not_put_the_providers_words_on_the_screen()
    {
        var result = await Extractor(new FakeChatClient(() => throw new HttpRequestException(Secret)))
            .ExtractAsync(OneImage);

        Assert.False(result.Success);
        AssertSafe(result.Error);
    }

    // ⚠️ The load-bearing property for a parse failure, and the one a leak cannot satisfy: the terminal
    // message is FIXED copy, not something derived from the failure. Two different bad outputs, one
    // sentence. Asserting the ABSENCE of deserializer text doesn't work here — these tests first pinned
    // "System." and "Path: $", which System.Text.Json never emits for this input (it says
    // 'n' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0.), so all three passed
    // while observing nothing. The mutation check is what caught that; green was what the defect produced.
    [Fact]
    public async Task An_unparseable_extraction_says_the_same_thing_whatever_the_model_returned()
    {
        // The retry contract's terminal failure used to interpolate the parse exception into the message.
        var a = await Extractor(new FakeChatClient(
            () => Responses.Text("nope"), () => Responses.Text("also nope"))).ExtractAsync(OneImage);
        var b = await Extractor(new FakeChatClient(
            () => Responses.Text("nope"), () => Responses.Text("""{ "merchant": 5 }"""))).ExtractAsync(OneImage);

        Assert.False(a.Success);
        Assert.False(b.Success);
        Assert.False(string.IsNullOrWhiteSpace(a.Error), "A failure must still say something to the person.");
        Assert.Equal(a.Error, b.Error);
    }

    // ---- Shelf census --------------------------------------------------------------------------

    private static AnthropicShelfCensusReader Reader(FakeChatClient client) =>
        new(client, Options.Create(new LlmOptions()), NullLogger<AnthropicShelfCensusReader>.Instance);

    private static readonly IReadOnlyList<ShelfPhoto> OnePhoto = [new([1, 2, 3], "image/jpeg")];

    [Fact]
    public async Task A_failed_census_call_does_not_put_the_providers_words_on_the_screen()
    {
        var result = await Reader(new FakeChatClient(() => throw new HttpRequestException(Secret)))
            .ReadAsync(OnePhoto);

        Assert.False(result.Success);
        AssertSafe(result.Error);
    }

    [Fact]
    public async Task An_unparseable_census_says_the_same_thing_whatever_the_model_returned()
    {
        var a = await Reader(new FakeChatClient(
            () => Responses.Text("nope"), () => Responses.Text("also nope"))).ReadAsync(OnePhoto);
        var b = await Reader(new FakeChatClient(
            () => Responses.Text("nope"), () => Responses.Text("""{ "items": 5 }"""))).ReadAsync(OnePhoto);

        Assert.False(a.Success);
        Assert.False(b.Success);
        Assert.False(string.IsNullOrWhiteSpace(a.Error), "A failure must still say something to the person.");
        Assert.Equal(a.Error, b.Error);
    }

    // ---- Recipe import -------------------------------------------------------------------------

    private static AnthropicRecipeImporter Importer(FakeChatClient client) =>
        new(client, Options.Create(new LlmOptions()), NullLogger<AnthropicRecipeImporter>.Instance);

    [Fact]
    public async Task An_unparseable_recipe_import_says_the_same_thing_whatever_the_model_returned()
    {
        // The importer's terminal copy was already fixed before 2026-09-19 — nothing here changed with
        // the other five sites. It is pinned anyway so the one service that got this right by accident
        // can't quietly drift into the shape its five siblings had to be fixed out of.
        var a = await Importer(new FakeChatClient(
            () => Responses.Text("nope"), () => Responses.Text("also nope")))
            .ImportFromImageAsync(new RecipePhoto([1, 2, 3], "image/jpeg"));
        var b = await Importer(new FakeChatClient(
            () => Responses.Text("nope"), () => Responses.Text("""{ "found": 5 }""")))
            .ImportFromImageAsync(new RecipePhoto([1, 2, 3], "image/jpeg"));

        Assert.False(a.Success);
        Assert.False(b.Success);
        Assert.False(string.IsNullOrWhiteSpace(a.Error), "A failure must still say something to the person.");
        Assert.Equal(a.Error, b.Error);
    }

    // ---- Speech --------------------------------------------------------------------------------

    private static FakeHttpMessageHandler Throwing() =>
        new(() => throw new HttpRequestException(Secret));

    private sealed record Creds(string ApiKey, string AgentId = "") : IVoiceCredentials;

    [Fact]
    public async Task A_failed_elevenlabs_synthesis_does_not_put_the_providers_words_on_the_screen()
    {
        var tts = new ElevenLabsTextToSpeech(
            new HttpClient(Throwing()) { BaseAddress = new Uri("https://api.elevenlabs.io") },
            Options.Create(new ElevenLabsOptions()), new Creds("test-key"),
            NullLogger<ElevenLabsTextToSpeech>.Instance);

        var result = await tts.SynthesizeAsync("Step 1. Sear the chicken.");

        Assert.False(result.Success);
        AssertSafe(result.Error);
    }

    [Fact]
    public async Task A_failed_transcription_does_not_put_the_providers_words_on_the_screen()
    {
        var stt = new ElevenLabsSpeechToText(
            new HttpClient(Throwing()) { BaseAddress = new Uri("https://api.elevenlabs.io") },
            Options.Create(new ElevenLabsOptions()), new Creds("test-key"),
            NullLogger<ElevenLabsSpeechToText>.Instance);

        var result = await stt.TranscribeAsync(new AudioClip([1, 2, 3], "audio/webm"));

        Assert.False(result.Success);
        AssertSafe(result.Error);
    }

    // ---- Pantry chat -----------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_chat_turn_does_not_put_the_providers_words_on_the_screen()
    {
        // ⚠️ The sixth site, and it was missed when the other five were converted on 2026-09-19 — its
        // failure reply interpolated ex.Message until a security review found it. It is the WORST of the
        // six to leak from: the dashboard chat box and PushToTalk both render this string verbatim, and a
        // chat turn is the surface a household uses most.
        var chat = new AnthropicPantryChat(
            new FakeChatClient(() => throw new HttpRequestException(Secret)),
            Options.Create(new LlmOptions()),
            new FakePantryStore(),
            NullLogger<AnthropicPantryChat>.Instance);

        var result = await chat.HandleAsync("what should I cook?");

        Assert.False(result.Success);
        AssertSafe(result.Reply);
    }

    [Fact]
    public async Task A_failed_local_synthesis_does_not_put_the_sidecars_words_on_the_screen()
    {
        // The sidecar is trusted local infrastructure, but its exception text still names a host and port.
        var tts = new LocalTextToSpeech(
            new HttpClient(Throwing()) { BaseAddress = new Uri("http://127.0.0.1:8880") },
            Options.Create(new LocalSpeechOptions()), NullLogger<LocalTextToSpeech>.Instance);

        var result = await tts.SynthesizeAsync("Step 1. Sear the chicken.");

        Assert.False(result.Success);
        AssertSafe(result.Error);
    }
}
