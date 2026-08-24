using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;
using ShelfAware.Core.Domain;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;
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
                ModelId = "claude-haiku-4-5", // so the cost lookup uses the Haiku rate, not the fallback
                Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 },
            });
        }

        // Nothing in the app streams TODAY — this exists so the decorator's streaming path can be
        // pinned, because a future streaming service must not be able to bypass metering through it.
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok") { ModelId = "claude-haiku-4-5" };
            yield return new ChatResponseUpdate
            {
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 })],
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FakeFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(AiProvider provider, string apiKey, string model, string? baseUrl = null) => client;
    }

    private (MeteredChatClient client, AiUsageMeter meter) Build(
        string keyMode, int? dailyCalls = null, long? dailyTokens = null, int? dailyMints = null,
        HouseholdTier tier = HouseholdTier.Free)
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
            new FakeEntitlements(tier),
            NullLogger<AiUsageMeter>.Instance);
        var byok = new ByokChatClient(settings, new FakeFactory(_provider));
        return (new MeteredChatClient(byok, settings, meter, Options.Create(new BillingOptions()),
            NullLogger<MeteredChatClient>.Instance), meter);
    }

    private static Task<ChatResponse> AskAsync(MeteredChatClient client) =>
        client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

    private async Task SeedDayAsync(string household, DateOnly day, int calls, long costMicros)
    {
        var previous = _db.HouseholdId;
        _db.HouseholdId = household;
        await using var db = _db.CreateDbContext();
        db.AiUsages.Add(new AiUsage { Day = day, Calls = calls, CostMicros = costMicros });
        await db.SaveChangesAsync();
        _db.HouseholdId = previous;
    }

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
        // Cost stamped from the Haiku rate: 100 in × $1/MTok (=100 micros) + 50 out × $5/MTok (=250) = 350.
        Assert.Equal(350, today.CostMicros);
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
    public async Task A_founder_is_exempt_from_the_call_cap_but_still_recorded()
    {
        // Same setup that blocks a Free household in At_the_call_cap... above — but a Founder rides the
        // host's key freely, and the call is still recorded (unlimited-but-recorded, like BYOK).
        await SeedTodayAsync("hh-test", calls: 5);
        var (client, meter) = Build("Managed", dailyCalls: 5, tier: HouseholdTier.Founder);

        var response = await AskAsync(client);

        Assert.Equal("ok", response.Text);
        Assert.Equal(1, _provider.Calls);                       // the provider WAS reached (the Free run throws)
        Assert.Equal(6, (await meter.GetTodayAsync()).Calls);   // seeded 5 + the Founder's recorded call
    }

    [Fact]
    public async Task A_founder_is_exempt_from_the_token_cap()
    {
        await SeedTodayAsync("hh-test", calls: 1, tokens: 10_000);
        var (client, _) = Build("Managed", dailyTokens: 10_000, tier: HouseholdTier.Founder);

        var response = await AskAsync(client);

        Assert.Equal("ok", response.Text);
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task A_founder_is_exempt_from_the_voice_mint_cap()
    {
        // At a Free household's mint cap of 2...
        var (_, meter) = Build("Managed", dailyMints: 2, tier: HouseholdTier.Founder);
        await meter.RecordVoiceSessionMintAsync();
        await meter.RecordVoiceSessionMintAsync();

        // ...a Founder may still mint (Free would be refused here — see Voice_session_mints_honor...).
        Assert.True(await meter.MayMintVoiceSessionAsync());
    }

    [Fact]
    public async Task A_byok_circuit_is_recorded_but_never_limited()
    {
        // Even with brutal limits configured, a BYOK visitor rides their own key freely — but the
        // usage still lands in their household's row, so the Settings panel can show what they spent.
        await SeedTodayAsync("hh-test", calls: 999);
        var (client, meter) = Build("Byok", dailyCalls: 1, dailyTokens: 1);

        var response = await AskAsync(client);

        Assert.Equal("ok", response.Text);
        Assert.Equal(1, _provider.Calls);
        var today = await meter.GetTodayAsync();
        Assert.Equal(1000, today.Calls);       // recorded on top of the seeded 999
        Assert.True(today.Tokens >= 150);      // the fake call's 100 in + 50 out landed too
    }

    [Fact]
    public async Task A_streamed_call_passes_updates_through_and_records_the_trailing_usage()
    {
        // The decorator's streaming half had NO test (the 7/30 audit's coverage read: 45%). It exists
        // so a future streaming service can't bypass metering — the quota gate runs up front and the
        // provider's trailing UsageContent lands in the household's row like any other call.
        var (client, meter) = Build("Managed", dailyCalls: 100);

        var texts = new List<string?>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            texts.Add(update.Text);

        Assert.Contains("ok", texts);
        var today = await meter.GetTodayAsync();
        Assert.Equal(1, today.Calls);
        Assert.Equal(150, today.Tokens);
    }

    [Fact]
    public async Task A_streamed_call_at_the_cap_throws_before_the_provider_yields_anything()
    {
        await SeedTodayAsync("hh-test", calls: 5);
        var (client, _) = Build("Managed", dailyCalls: 5);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
        });
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task A_metering_write_failure_never_fails_the_users_answer()
    {
        // The deliberate catch in RecordAsync: the user already has their response, so a bookkeeping
        // failure logs and under-counts rather than blowing up the reply. Byok mode so no quota read
        // runs up front; disposing the TestDb makes the usage write throw exactly as a dead DB would.
        var (client, _) = Build("Byok");
        _db.Dispose();

        var response = await AskAsync(client);

        Assert.Equal("ok", response.Text);
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task Recent_usage_lists_days_newest_first_for_the_settings_panel()
    {
        var (client, meter) = Build("Managed");
        await AskAsync(client);

        var recent = await meter.GetRecentAsync(days: 14);

        var today = Assert.Single(recent);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), today.Day);
        Assert.Equal(1, today.Calls);
        Assert.Equal(100, today.InputTokens);
        Assert.Equal(50, today.OutputTokens);
        Assert.Equal(150, today.Tokens);
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
    public async Task Monthly_usage_rolls_up_by_calendar_month_with_cost()
    {
        // Two days in a definite PAST month (so "today" can't interfere), summed into one month row —
        // the "is it steady month to month?" view.
        var today = DateOnly.FromDateTime(DateTime.Today);
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        await SeedDayAsync("hh-test", new DateOnly(lastMonth.Year, lastMonth.Month, 3), calls: 2, costMicros: 1000);
        await SeedDayAsync("hh-test", new DateOnly(lastMonth.Year, lastMonth.Month, 15), calls: 3, costMicros: 2500);

        var (_, meter) = Build("Managed");
        var months = await meter.GetMonthlyAsync(3);

        var m = months.Single(x => x.Year == lastMonth.Year && x.Month == lastMonth.Month);
        Assert.Equal(5, m.Calls);           // 2 + 3, rolled up across the two days
        Assert.Equal(3500, m.CostMicros);   // 1000 + 2500
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
