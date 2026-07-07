using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Llm;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Managed-mode metering: quotas guard the HOST's wallet, BYOK circuits are never touched, and
/// households meter separately. Real SQLite via TestDb; the provider call is a scripted fake.
/// </summary>
public class MeteredChatClientTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ScriptedChatClient _provider = new();

    public void Dispose() => _db.Dispose();

    private sealed class ScriptedChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The app's AI services don't stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FakeFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(AiProvider provider, string apiKey, string model, string? baseUrl = null) => client;
    }

    private (MeteredChatClient client, AiUsageMeter meter) Build(
        string keyMode, int? dailyCalls = null, long? dailyTokens = null, int? dailyMints = null)
    {
        var llm = Options.Create(new LlmOptions
        {
            ApiKey = "server-key",
            KeyMode = keyMode,
            DailyCallLimit = dailyCalls,
            DailyTokenLimit = dailyTokens,
        });
        var settings = new CircuitAiSettings(llm);
        var meter = new AiUsageMeter(_db, llm,
            Options.Create(new ElevenLabsOptions { DailySignedUrlLimit = dailyMints }),
            NullLogger<AiUsageMeter>.Instance);
        var byok = new ByokChatClient(settings, new FakeFactory(_provider));
        return (new MeteredChatClient(byok, settings, meter, NullLogger<MeteredChatClient>.Instance), meter);
    }

    private static Task<ChatResponse> AskAsync(MeteredChatClient client) =>
        client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

    private async Task SeedTodayAsync(string household, int calls = 0, long tokens = 0, int mints = 0)
    {
        var previous = _db.HouseholdId;
        _db.HouseholdId = household;
        await using var db = _db.CreateDbContext();
        db.AiUsages.Add(new AiUsage
        {
            Day = DateOnly.FromDateTime(DateTime.Today),
            Calls = calls,
            InputTokens = tokens,
            VoiceSessionMints = mints,
        });
        await db.SaveChangesAsync();
        _db.HouseholdId = previous;
    }

    [Fact]
    public async Task A_managed_call_passes_through_and_records_calls_and_tokens()
    {
        var (client, meter) = Build("Managed", dailyCalls: 100);

        var response = await AskAsync(client);

        Assert.Equal("ok", response.Text);
        Assert.Equal(1, _provider.Calls);
        var today = await meter.GetTodayAsync();
        Assert.Equal(1, today.Calls);
        Assert.Equal(150, today.Tokens);
    }

    [Fact]
    public async Task Usage_accumulates_across_calls()
    {
        var (client, meter) = Build("Managed");

        await AskAsync(client);
        await AskAsync(client);

        var today = await meter.GetTodayAsync();
        Assert.Equal(2, today.Calls);
        Assert.Equal(300, today.Tokens);
    }

    [Fact]
    public async Task At_the_call_cap_the_provider_is_never_reached()
    {
        await SeedTodayAsync("hh-test", calls: 5);
        var (client, _) = Build("Managed", dailyCalls: 5);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(client));

        Assert.Contains("allowance", ex.Message);
        Assert.Contains("your own key", ex.Message);
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task At_the_token_cap_the_provider_is_never_reached()
    {
        await SeedTodayAsync("hh-test", calls: 1, tokens: 10_000);
        var (client, _) = Build("Managed", dailyTokens: 10_000);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(client));
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task A_byok_circuit_is_never_metered_or_limited()
    {
        // Even with brutal limits configured, a BYOK visitor rides their own key freely.
        await SeedTodayAsync("hh-test", calls: 999);
        var (client, meter) = Build("Byok", dailyCalls: 1, dailyTokens: 1);

        var response = await AskAsync(client);

        Assert.Equal("ok", response.Text);
        Assert.Equal(1, _provider.Calls);
        Assert.Equal(999, (await meter.GetTodayAsync()).Calls); // unchanged — nothing recorded
    }

    [Fact]
    public async Task Households_meter_separately_and_one_cap_does_not_block_another()
    {
        await SeedTodayAsync("hh-a", calls: 5);

        _db.HouseholdId = "hh-a";
        var (blockedClient, _) = Build("Managed", dailyCalls: 5);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(blockedClient));

        _db.HouseholdId = "hh-b";
        var (freshClient, freshMeter) = Build("Managed", dailyCalls: 5);
        await AskAsync(freshClient);

        Assert.Equal(1, (await freshMeter.GetTodayAsync()).Calls);
    }

    [Fact]
    public async Task Voice_session_mints_honor_their_daily_quota()
    {
        var (_, meter) = Build("Managed", dailyMints: 2);

        Assert.True(await meter.MayMintVoiceSessionAsync());
        await meter.RecordVoiceSessionMintAsync();
        await meter.RecordVoiceSessionMintAsync();

        Assert.False(await meter.MayMintVoiceSessionAsync());
        Assert.Equal(2, (await meter.GetTodayAsync()).VoiceSessionMints);

        // No configured limit = unlimited (the self-host default).
        var (_, unlimited) = Build("Managed");
        Assert.True(await unlimited.MayMintVoiceSessionAsync());
    }
}
