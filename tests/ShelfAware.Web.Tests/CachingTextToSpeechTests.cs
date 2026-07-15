using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Speech;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Drives the speech cache against a counting fake provider over a real temp directory — the point of
/// the cache is "how many times did we actually pay ElevenLabs", so the assertions are call counts.
/// </summary>
public sealed class CachingTextToSpeechTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shelfaware-tts-cache-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private CachingTextToSpeech Cache(ITextToSpeech inner) =>
        new(inner, _dir, NullLogger<CachingTextToSpeech>.Instance);

    /// <summary>A provider that counts what it was asked to synthesize and hands back canned audio.</summary>
    private sealed class FakeTts(string fingerprint = "voice-a", bool succeed = true) : ITextToSpeech
    {
        public int Calls { get; private set; }
        public List<string> Texts { get; } = [];

        public string OutputFingerprint { get; set; } = fingerprint;
        public string OutputMediaType => "audio/mpeg";

        public Task<TextToSpeechResult> SynthesizeAsync(
            string text, SpeechContext? context = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Texts.Add(text);
            return Task.FromResult(succeed
                ? TextToSpeechResult.Ok([1, 2, 3, 4], "audio/mpeg")
                : TextToSpeechResult.Fail("provider is down"));
        }
    }

    // The whole point: a recipe costs one synthesis, however many times it's read.
    [Fact]
    public async Task The_same_step_is_synthesized_once_and_served_from_disk_after_that()
    {
        var inner = new FakeTts();
        var cache = Cache(inner);

        var first = await cache.SynthesizeAsync("Step 1. Sear the chicken.");
        var second = await cache.SynthesizeAsync("Step 1. Sear the chicken.");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Audio, second.Audio);
        Assert.Equal("audio/mpeg", second.MediaType);
        Assert.Equal(1, inner.Calls);
    }

    // A cache hit needs no key, which is what lets seeded/demo recipes talk for a visitor who brought none.
    [Fact]
    public async Task A_cached_clip_is_served_without_consulting_the_provider_at_all()
    {
        var warm = new FakeTts();
        await Cache(warm).SynthesizeAsync("Step 1. Sear the chicken.");

        // A provider that would fail (e.g. no API key on this circuit) must never be reached.
        var keyless = new FakeTts(succeed: false);
        var result = await Cache(keyless).SynthesizeAsync("Step 1. Sear the chicken.");

        Assert.True(result.Success);
        Assert.Equal(0, keyless.Calls);
    }

    [Fact]
    public async Task Different_text_is_a_different_clip()
    {
        var inner = new FakeTts();
        var cache = Cache(inner);

        await cache.SynthesizeAsync("Step 1. Sear the chicken.");
        await cache.SynthesizeAsync("Step 2. Rest it.");

        Assert.Equal(2, inner.Calls);
    }

    // previous_text/next_text change the audio, so they have to change the key — otherwise we'd serve a
    // clip voiced for a different position in the recipe.
    [Fact]
    public async Task The_same_sentence_in_a_different_position_is_a_different_clip()
    {
        var inner = new FakeTts();
        var cache = Cache(inner);

        await cache.SynthesizeAsync("Stir well.", new SpeechContext(Previous: "Step 1. Add oil."));
        await cache.SynthesizeAsync("Stir well.", new SpeechContext(Previous: "Step 4. Add cream."));

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Context_and_no_context_are_different_clips()
    {
        var inner = new FakeTts();
        var cache = Cache(inner);

        await cache.SynthesizeAsync("Stir well.");
        await cache.SynthesizeAsync("Stir well.", new SpeechContext(Next: "Step 3. Serve."));

        Assert.Equal(2, inner.Calls);
    }

    // Changing the voice, model, speed — or how we spell text out — must retire the old audio rather
    // than serve yesterday's pronunciation forever.
    [Fact]
    public async Task A_changed_output_fingerprint_retires_the_cached_clip()
    {
        var inner = new FakeTts();
        await Cache(inner).SynthesizeAsync("Step 1. Sear the chicken.");

        inner.OutputFingerprint = "voice-b";
        await Cache(inner).SynthesizeAsync("Step 1. Sear the chicken.");

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_failed_synthesis_is_not_cached()
    {
        var failing = new FakeTts(succeed: false);
        var cache = Cache(failing);

        var first = await cache.SynthesizeAsync("Step 1. Sear the chicken.");
        var second = await cache.SynthesizeAsync("Step 1. Sear the chicken.");

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.Equal(2, failing.Calls); // retried, not remembered as a failure
    }

    // Blank text is the provider's rule to state, not the cache's to guess at.
    [Fact]
    public async Task Blank_text_is_left_to_the_provider()
    {
        var inner = new FakeTts();

        await Cache(inner).SynthesizeAsync("   ");

        Assert.Equal(1, inner.Calls);
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir, "*.audio").Length > 0);
    }

    [Fact]
    public async Task A_cancelled_call_propagates_and_writes_nothing()
    {
        var inner = new FakeTts();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Cache(inner).SynthesizeAsync("Step 1. Sear the chicken.", null, cts.Token));

        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir, "*.audio").Length > 0);
    }

    [Fact]
    public void Trim_leaves_a_cache_that_is_under_budget_alone()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "a.audio"), new byte[100]);

        var removed = CachingTextToSpeech.Trim(_dir, maxBytes: 1000, NullLogger.Instance);

        Assert.Equal(0, removed);
        Assert.Single(Directory.GetFiles(_dir, "*.audio"));
    }

    [Fact]
    public void Trim_drops_the_oldest_clips_until_the_cache_fits()
    {
        Directory.CreateDirectory(_dir);
        foreach (var (name, age) in new[] { ("old.audio", 3), ("middle.audio", 2), ("new.audio", 1) })
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, new byte[100]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-age));
        }

        // Room for one 100-byte clip: the two oldest go.
        var removed = CachingTextToSpeech.Trim(_dir, maxBytes: 100, NullLogger.Instance);

        Assert.Equal(2, removed);
        Assert.Equal(["new.audio"], Directory.GetFiles(_dir, "*.audio").Select(Path.GetFileName));
    }

    [Fact]
    public void Trim_on_a_cache_that_was_never_created_is_a_no_op()
    {
        Assert.Equal(0, CachingTextToSpeech.Trim(Path.Combine(_dir, "nope"), maxBytes: 10, NullLogger.Instance));
    }

    // Drives the REAL composition root, not a hand-rolled copy of it: whatever asks for ITextToSpeech
    // must get the cache. If someone later re-registers the provider directly, caching would silently
    // stop and the only symptom would be a bigger bill.
    [Fact]
    public void Everything_that_asks_for_text_to_speech_gets_the_cache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IVoiceCredentials>(_ => new StubVoiceCredentials());
        services.AddSpeech(new ConfigurationBuilder().Build(), _dir);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsType<CachingTextToSpeech>(scope.ServiceProvider.GetRequiredService<ITextToSpeech>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISpeechToText>());
    }

    private sealed class StubVoiceCredentials : IVoiceCredentials
    {
        public string ApiKey => "";
        public string AgentId => "";
    }
}
