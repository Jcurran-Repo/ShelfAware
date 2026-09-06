using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;
using ShelfAware.Core.Domain;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Managed-mode metering: quotas guard the HOST's wallet, BYOK circuits are never touched, and
/// households meter separately. Real SQLite via TestDb; the provider call is a scripted fake.
/// </summary>
public class MeteredChatClientTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly TestAuthDb _authDb = new(); // the ledger lives in auth.db
    private readonly ScriptedChatClient _provider = new();

    public void Dispose()
    {
        _db.Dispose();
        _authDb.Dispose();
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        public int Calls { get; private set; }

        /// <summary>The model the fake REPORTS on its response. Defaults to Haiku so the cost lookup uses
        /// the Haiku rate; a test sets it null to exercise the requested-model fallback.</summary>
        public string? ResponseModelId { get; set; } = "claude-haiku-4-5";

        /// <summary>When true, the provider call throws OperationCanceledException instead of returning —
        /// the "client dropped the socket mid-flight" scenario (no response, so no tokens/cost).</summary>
        public bool ThrowCancelled { get; set; }

        /// <summary>When true, the provider call throws a NON-cancellation exception (a 429/5xx/connection
        /// error) before any cost — the "outage" scenario the reserved call must be RELEASED for.</summary>
        public bool ThrowRefusal { get; set; }

        /// <summary>When true, the provider call throws <see cref="TaskCanceledException"/> — the type the
        /// real SDK produces for a timeout/abort (it derives from OperationCanceledException), to prove a
        /// timeout STAYS counted like a plain cancel.</summary>
        public bool ThrowTaskCancelled { get; set; }

        /// <summary>The streaming twins of the throw flags — surfaced on the first MoveNextAsync so the
        /// decorator's manual-enumerator catch can be exercised for a refusal (released), a plain abort, and
        /// a real-SDK timeout (both counted).</summary>
        public bool ThrowRefusalStreaming { get; set; }
        public bool ThrowCancelledStreaming { get; set; }
        public bool ThrowTaskCancelledStreaming { get; set; }

        /// <summary>When set, the stream yields this many updates and THEN throws a non-cancellation error
        /// mid-stream — the "provider streamed a partial answer, then the connection broke" scenario, to
        /// prove a post-output break STAYS counted (billable work was done).</summary>
        public int? ThrowAfterStreamingUpdates { get; set; }

        /// <summary>When true, the stream yields normally but its DISPOSAL throws (a broken HTTP response
        /// closing) — to prove the decorator records usage BEFORE it disposes the enumerator.</summary>
        public bool ThrowOnStreamDispose { get; set; }

        /// <summary>When set, the provider cancels this source just as it returns the answer — the "client
        /// dropped the instant the response landed" scenario, to prove the tail record runs uncancellably.</summary>
        public CancellationTokenSource? CancelWhenReturning { get; set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (ThrowCancelled) throw new OperationCanceledException();
            if (ThrowTaskCancelled) throw new TaskCanceledException();
            if (ThrowRefusal) throw new InvalidOperationException("the provider refused this call");
            CancelWhenReturning?.Cancel(); // the caller's token is now cancelled, but the answer is ready
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                ModelId = ResponseModelId,
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
            // Surface a refusal/abort on the first MoveNextAsync (the throw runs when enumeration starts).
            if (ThrowRefusalStreaming) throw new InvalidOperationException("the provider refused this stream");
            if (ThrowCancelledStreaming) throw new OperationCanceledException();
            if (ThrowTaskCancelledStreaming) throw new TaskCanceledException();
            await Task.Yield();
            if (ThrowAfterStreamingUpdates is int n)
            {
                for (var i = 0; i < n; i++)
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "chunk") { ModelId = ResponseModelId };
                // The stream broke AFTER producing output — the provider did billable work, so the decorator
                // must keep the call counted (yieldedAny is true by now).
                throw new InvalidOperationException("the stream broke mid-answer");
            }
            try
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "ok") { ModelId = ResponseModelId };
                yield return new ChatResponseUpdate
                {
                    Contents = [new UsageContent(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 })],
                };
            }
            finally
            {
                // The provider's stream fails to close cleanly — the throw runs when the enumerator is
                // disposed (the consumer stopping early), so the decorator's dispose is what throws.
                if (ThrowOnStreamDispose) throw new System.IO.IOException("closing the stream failed");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class FakeFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(AiProvider provider, string apiKey, string model, string? baseUrl = null) => client;
    }

    /// <summary>Builds nothing — Create throws EAGERLY (the keyless / blank-key boot), so the provider call
    /// never happens. ByokChatClient calls this synchronously at request time, so the streaming path's
    /// enumerator creation throws before any output, exercising the eager-refusal release.</summary>
    private sealed class ThrowingFactory : IChatClientFactory
    {
        public IChatClient Create(AiProvider provider, string apiKey, string model, string? baseUrl = null)
            => throw new InvalidOperationException("no API key configured — add one in Settings");
    }

    /// <summary>Wraps the real household factory but throws on the FIRST context request, then delegates —
    /// models a transient write failure that hits the RESERVE but clears before the release, the only shape
    /// that can drive the counter negative (F2). The reserve is the first factory call when no per-household
    /// cap runs at the gate, so failing #1 fails exactly the reserve.</summary>
    private sealed class FailFirstHouseholdDbFactory(IHouseholdDbFactory inner) : IHouseholdDbFactory
    {
        private int _calls;
        public Task<ShelfAwareDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (System.Threading.Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("transient DB failure on the reserve write");
            return inner.CreateDbContextAsync(cancellationToken);
        }
    }

    /// <summary>Wraps the auth.db factory and throws on the Nth context request, then delegates — models a
    /// transient failure on a specific demo-meter step (the gate read is call #1, the reserve write #2), to
    /// pin the box-wide half of the balanced release.</summary>
    private sealed class FailNthAuthDbFactory(IDbContextFactory<AuthDbContext> inner, int failOn) : IDbContextFactory<AuthDbContext>
    {
        private int _calls;
        public AuthDbContext CreateDbContext() => inner.CreateDbContext();
        public Task<AuthDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (System.Threading.Interlocked.Increment(ref _calls) == failOn)
                throw new InvalidOperationException("transient auth.db failure on the demo reserve write");
            return inner.CreateDbContextAsync(cancellationToken);
        }
    }

    private (MeteredChatClient client, AiUsageMeter meter) Build(
        string keyMode, int? dailyCalls = null, long? dailyTokens = null, int? dailyMints = null,
        HouseholdTier tier = HouseholdTier.Free, long balanceMicros = 100_000_000, bool paymentsEnabled = true,
        int? demoCap = null, bool factoryThrows = false, bool meterReserveFailsFirst = false,
        int? demoReserveFailsOnCall = null, ILogger<MeteredChatClient>? clientLogger = null)
    {
        var llm = Options.Create(new LlmOptions
        {
            ApiKey = "server-key",
            KeyMode = keyMode,
            DailyCallLimit = dailyCalls,
            DailyTokenLimit = dailyTokens,
        });
        var settings = new CircuitAiSettings(llm);
        // Default "plenty" so a recording/metering test's managed call is allowed by the phase-4b gate; a
        // gate test sets balanceMicros: 0 to exercise the refusal.
        var entitlements = new FakeEntitlements(tier) { BalanceMicros = balanceMicros };
        var payments = Options.Create(new ShelfAware.Web.Billing.PaymentsOptions { Enabled = paymentsEnabled });
        // meterReserveFailsFirst faults ONLY the first usage-row write (the reserve), to pin the balanced
        // release; the returned meter reads through the same wrapper, whose later calls delegate to _db.
        IHouseholdDbFactory meterFactory = meterReserveFailsFirst ? new FailFirstHouseholdDbFactory(_db) : _db;
        var meter = new AiUsageMeter(meterFactory, llm,
            Options.Create(new ElevenLabsOptions { DailySignedUrlLimit = dailyMints }),
            payments,
            entitlements,
            NullLogger<AiUsageMeter>.Instance);
        // factoryThrows models a keyless/blank-key boot: the provider client can't be built at all.
        var byok = new ByokChatClient(settings, factoryThrows ? new ThrowingFactory() : new FakeFactory(_provider));
        // The box-wide demo valve. Unconfigured by default (a no-op, so the per-household metering tests are
        // unaffected); a demo-cap test passes demoCap to exercise the box-wide enforcement here — the point
        // MeteredChatClient actually bounds the host wallet, which the fable gate found untested.
        // demoReserveFailsOnCall faults the Nth auth.db context request the demo meter makes (the gate read is
        // #1, the reserve write is #2), to pin the box-wide half of the balanced release.
        IDbContextFactory<AuthDbContext> demoFactory =
            demoReserveFailsOnCall is int n ? new FailNthAuthDbFactory(_authDb, n) : _authDb;
        var demoMeter = new DemoUsageMeter(
            demoFactory, Options.Create(new DemoOptions { DailyGlobalCallLimit = demoCap }), NullLogger<DemoUsageMeter>.Instance);
        var client = new MeteredChatClient(byok, settings, meter, demoMeter, Options.Create(new BillingOptions()),
            payments,
            new CreditLedger(_authDb, Options.Create(new BillingOptions())), entitlements, new FakeCurrentHousehold("hh-test"),
            clientLogger ?? NullLogger<MeteredChatClient>.Instance);
        return (client, meter);
    }

    private static Task<ChatResponse> AskAsync(MeteredChatClient client) =>
        client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

    private async Task<int> DemoCallsTodayAsync()
    {
        await using var db = _authDb.CreateDbContext();
        var row = await db.DemoUsage.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Day == DateOnly.FromDateTime(DateTime.Today));
        return row?.Calls ?? 0;
    }

    private async Task SeedDemoTodayAsync(int calls)
    {
        await using var db = _authDb.CreateDbContext();
        db.DemoUsage.Add(new DemoUsageDay { Day = DateOnly.FromDateTime(DateTime.Today), Calls = calls });
        await db.SaveChangesAsync();
    }

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
    public async Task Cost_falls_back_to_the_requested_model_when_the_provider_reports_none()
    {
        // The provider echoes no model id; the cost must price at the REQUESTED model (Haiku, 350), not
        // AiPricing's priciest-tier fallback (1750) — so a missing reported id can't read 5× high.
        _provider.ResponseModelId = null;
        var (client, meter) = Build("Managed");

        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], new ChatOptions { ModelId = "claude-haiku-4-5" });

        Assert.Equal(350, (await meter.GetTodayAsync()).CostMicros);
    }

    [Fact]
    public async Task A_managed_non_founder_call_draws_the_credit_balance_down()
    {
        var (client, _) = Build("Managed", tier: HouseholdTier.Free);

        await AskAsync(client);

        // Cost 350 micros (Haiku 100/50) × 1.65 markup = 578 retail micros, stored as a consumption.
        Assert.Equal(-578, await new CreditLedger(_authDb, Options.Create(new BillingOptions())).GetBalanceMicrosAsync("hh-test"));
    }

    [Fact]
    public async Task A_usage_write_failure_still_records_the_credit_consumption()
    {
        // The AiUsage (pantry) write and the ledger (auth) write are INDEPENDENT best-effort: a pantry
        // failure must not silently drop the MONEY write. Dispose the pantry db so BOTH the gate's cap-read
        // AND the tail usage-write throw; the gate read is best-effort too (an infra blip mustn't block a
        // legitimate call — credit in auth.db is the real bound), and the auth ledger is untouched, so the
        // call runs and consumption still lands.
        var (client, _) = Build("Managed", tier: HouseholdTier.Free);
        _db.Dispose();

        var response = await AskAsync(client);

        Assert.Equal("ok", response.Text);  // the gate allowed past the dead pantry; the user still got their answer
        Assert.Equal(-578, await new CreditLedger(_authDb, Options.Create(new BillingOptions())).GetBalanceMicrosAsync("hh-test")); // money write landed
    }

    [Fact]
    public async Task A_founder_call_records_cost_but_no_credit_consumption()
    {
        var (client, meter) = Build("Managed", tier: HouseholdTier.Founder);

        await AskAsync(client);

        Assert.Equal(350, (await meter.GetTodayAsync()).CostMicros);                        // cost still recorded
        Assert.Equal(0, await new CreditLedger(_authDb, Options.Create(new BillingOptions())).GetBalanceMicrosAsync("hh-test"));  // but no credit drawn
    }

    [Fact]
    public async Task A_managed_call_with_billing_off_records_cost_but_no_credit_consumption()
    {
        // ⚠️ The credit system is on or off as ONE thing: on a managed box with no Payments config (dev /
        // self-host / family — §7 "unlimited by default"), USAGE is still recorded but the ledger is NOT
        // drawn — otherwise a billing-off box accrues an invisible negative balance that flipping billing on
        // would later enforce (the partial-conversion the re-gate caught). Mirrors IsAiAllowedAsync's skip.
        var (client, meter) = Build("Managed", tier: HouseholdTier.Free, paymentsEnabled: false);

        await AskAsync(client);

        Assert.Equal(350, (await meter.GetTodayAsync()).CostMicros);                        // usage still recorded
        Assert.Equal(0, await new CreditLedger(_authDb, Options.Create(new BillingOptions())).GetBalanceMicrosAsync("hh-test"));  // no ledger drawdown
    }

    [Fact]
    public async Task A_byok_call_records_no_credit_consumption()
    {
        var (client, _) = Build("Byok");

        await AskAsync(client);

        Assert.Equal(0, await new CreditLedger(_authDb, Options.Create(new BillingOptions())).GetBalanceMicrosAsync("hh-test")); // their key, their wallet
    }

    // ---- The AI-allowed gate: phase 4b refuses a managed call the household can't pay for ----

    [Fact]
    public async Task A_managed_household_with_no_credit_is_refused_before_the_call()
    {
        var (client, _) = Build("Managed", tier: HouseholdTier.Free, balanceMicros: 0);

        await Assert.ThrowsAsync<AiCreditsExhaustedException>(() => AskAsync(client));

        Assert.Equal(0, _provider.Calls); // refused BEFORE the provider call — nothing spent
        Assert.Equal(0, await new CreditLedger(_authDb, Options.Create(new BillingOptions())).GetBalanceMicrosAsync("hh-test")); // nothing recorded
    }

    [Fact]
    public async Task A_managed_household_with_credit_is_allowed()
    {
        var (client, _) = Build("Managed", tier: HouseholdTier.Free, balanceMicros: 1_000_000);

        var response = await AskAsync(client);

        Assert.Equal("ok", response.Text);
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task A_founder_with_no_credit_is_never_refused()
    {
        var (client, _) = Build("Managed", tier: HouseholdTier.Founder, balanceMicros: 0);

        var response = await AskAsync(client);

        Assert.Equal("ok", response.Text); // unlimited — the balance is never consulted
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task A_BYOK_circuit_with_no_credit_is_never_gated()
    {
        var (client, _) = Build("Byok", tier: HouseholdTier.Free, balanceMicros: 0);

        var response = await AskAsync(client);

        Assert.Equal("ok", response.Text); // their key, their wallet — no managed gate
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task The_streaming_path_also_refuses_an_exhausted_managed_household()
    {
        // The gate guards the streaming path too, so a future streaming service can't slip past it.
        var (client, _) = Build("Managed", tier: HouseholdTier.Free, balanceMicros: 0);

        await Assert.ThrowsAsync<AiCreditsExhaustedException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
        });

        Assert.Equal(0, _provider.Calls); // refused before the stream opened
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
    public async Task A_per_household_call_limit_of_zero_blocks_the_first_call()
    {
        // 0 is a kill switch — it must block before any row exists (the null-row was early-returning as
        // "allowed" before). No seed, so this is the first call of the day.
        var (client, _) = Build("Managed", dailyCalls: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(client));
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

    // ---- Box-wide demo valve enforcement (the fable gate found this whole point untested) ----

    [Fact]
    public async Task The_box_wide_demo_cap_blocks_a_managed_call_and_counts_the_attempt_at_the_gate()
    {
        var (client, _) = Build("Managed", demoCap: 1);

        await AskAsync(client);                        // first call: allowed, reserved at the gate
        Assert.Equal(1, _provider.Calls);
        Assert.Equal(1, await DemoCallsTodayAsync());  // the box-wide counter ticked

        // The second is over the box-wide cap: refused, provider never reached (this is the enforcement
        // point that actually bounds the host wallet — deleting the gate check leaves this passing wrongly).
        await Assert.ThrowsAsync<DemoDailyCapException>(() => AskAsync(client));
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task A_byok_call_never_touches_the_box_wide_demo_valve()
    {
        var (client, _) = Build("Byok", demoCap: 1);

        await AskAsync(client);
        await AskAsync(client);                        // both go through — BYOK rides its own key

        Assert.Equal(2, _provider.Calls);
        Assert.Equal(0, await DemoCallsTodayAsync());  // the host-key valve never counted them
    }

    // ---- The reserve is uncancellable: a fire-and-abort visitor can't dodge the caps ----

    [Fact]
    public async Task A_call_aborted_mid_flight_still_counts_against_the_per_household_and_box_wide_caps()
    {
        _provider.ThrowCancelled = true;               // the client drops before the provider returns
        var (client, meter) = Build("Managed", dailyCalls: 5, demoCap: 5);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AskAsync(client));

        // No response means no tokens/cost — but the CALL was reserved at the gate, so a fire-and-abort
        // visitor can't make host-key calls the caps never see.
        var today = await meter.GetTodayAsync();
        Assert.Equal(1, today.Calls);
        Assert.Equal(0, today.Tokens);
        Assert.Equal(1, await DemoCallsTodayAsync());
    }

    [Fact]
    public async Task Tokens_cost_and_credit_are_recorded_even_if_the_caller_drops_after_the_response()
    {
        using var cts = new CancellationTokenSource();
        _provider.CancelWhenReturning = cts;           // the answer lands, the client drops in the same instant
        var (client, meter) = Build("Managed", tier: HouseholdTier.Free);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token);

        // The tail record runs on CancellationToken.None, so a caller who cancelled AFTER the answer can't
        // dodge the token/cost/credit write (the completed-then-abort window fable flagged).
        var today = await meter.GetTodayAsync();
        Assert.Equal(1, today.Calls);
        Assert.Equal(150, today.Tokens);
        Assert.Equal(350, today.CostMicros);
        // …and the money write landed too: 350 × 1.65 = 578 retail micros drawn.
        Assert.Equal(-578, await new CreditLedger(_authDb, Options.Create(new BillingOptions())).GetBalanceMicrosAsync("hh-test"));
    }

    // ---- Release-on-refusal: an OUTAGE gives the reserved call back; an ABORT does not ----

    [Fact]
    public async Task A_refused_managed_call_gives_the_reserved_call_back_on_both_caps()
    {
        _provider.ThrowRefusal = true;                 // provider 429/5xx before any cost
        var (client, meter) = Build("Managed", dailyCalls: 5, demoCap: 5);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(client));

        // The call was reserved at the gate, then RELEASED when the provider refused — so an outage
        // doesn't burn the per-household or box-wide caps on requests that never ran. (Contrast the abort
        // test above, which stays counted: the filter is `ex is not OperationCanceledException`.)
        Assert.Equal(0, (await meter.GetTodayAsync()).Calls);
        Assert.Equal(0, await DemoCallsTodayAsync());
    }

    [Fact]
    public async Task A_refused_managed_stream_gives_the_reserved_call_back_on_both_caps()
    {
        _provider.ThrowRefusalStreaming = true;
        var (client, meter) = Build("Managed", dailyCalls: 5, demoCap: 5);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
        });

        Assert.Equal(0, (await meter.GetTodayAsync()).Calls);
        Assert.Equal(0, await DemoCallsTodayAsync());
    }

    [Fact]
    public async Task A_streamed_call_aborted_mid_flight_stays_counted()
    {
        // The streaming filter's complement: an OperationCanceledException (the consumer dropped) reached
        // the provider and cost the key, so it must NOT be released — it stays counted like the non-stream
        // abort. This is what pins `ex is not OperationCanceledException` on the streaming catch.
        _provider.ThrowCancelledStreaming = true;
        var (client, meter) = Build("Managed", dailyCalls: 5, demoCap: 5);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
        });

        Assert.Equal(1, (await meter.GetTodayAsync()).Calls);
        Assert.Equal(1, await DemoCallsTodayAsync());
    }

    // ---- The paid-box default call cap: a PAID box is never accidentally unbounded ----

    [Fact]
    public async Task A_paid_box_with_no_configured_call_limit_still_has_a_default_cap()
    {
        // Payments ON but the operator set no Llm:DailyCallLimit — the box must NOT run unbounded (a
        // griefer abort-spamming would otherwise burn to the key's hard spend limit). Seed the default
        // cap's worth of calls and the next is refused before the provider.
        await SeedTodayAsync("hh-test", calls: 1000);
        var (client, _) = Build("Managed", tier: HouseholdTier.Free, paymentsEnabled: true); // no dailyCalls

        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(client));
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task A_billing_off_box_with_no_configured_call_limit_stays_unbounded()
    {
        // The complement: no Payments config (self-host / family / dev) means no accidental cap — the
        // default applies only to a PAID box. The same seed that blocks the paid box above sails through.
        await SeedTodayAsync("hh-test", calls: 1000);
        var (client, _) = Build("Managed", tier: HouseholdTier.Free, paymentsEnabled: false); // no dailyCalls

        var response = await AskAsync(client);
        Assert.Equal("ok", response.Text);
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task A_voice_mint_cap_of_zero_blocks_the_first_mint()
    {
        // cap=0 is a kill switch for voice too — it must block before any row exists (the coalesce fix;
        // a null row used to read as "allowed" and admit one mint).
        var (_, meter) = Build("Managed", dailyMints: 0);
        Assert.False(await meter.MayMintVoiceSessionAsync());
    }

    // ---- Release keys on "no output yet", not exception type: a call that DID work stays counted ----

    [Fact]
    public async Task A_streamed_call_that_breaks_after_producing_output_stays_counted()
    {
        // F1: a stream that yields updates and THEN breaks (non-OCE) did billable work — the provider
        // produced a partial answer — so the call must NOT be released, unlike a break BEFORE any output
        // (A_refused_managed_stream...). Two updates land, then the provider errors mid-stream.
        _provider.ThrowAfterStreamingUpdates = 2;
        var (client, meter) = Build("Managed", dailyCalls: 5, demoCap: 5);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
        });

        Assert.Equal(1, (await meter.GetTodayAsync()).Calls); // yieldedAny was true → not released
        Assert.Equal(1, await DemoCallsTodayAsync());
    }

    [Fact]
    public async Task A_streamed_provider_timeout_TaskCanceledException_stays_counted()
    {
        // The streaming twin of A_provider_timeout... — a TaskCanceledException on the first MoveNextAsync is
        // a timeout that reached the provider, so the streaming inner catch must keep it counted (it derives
        // from OperationCanceledException), never released.
        _provider.ThrowTaskCancelledStreaming = true;
        var (client, meter) = Build("Managed", dailyCalls: 5, demoCap: 5);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
        });

        Assert.Equal(1, (await meter.GetTodayAsync()).Calls);
        Assert.Equal(1, await DemoCallsTodayAsync());
    }

    [Fact]
    public async Task A_stream_records_usage_before_a_throwing_dispose()
    {
        // F5: the finally records tokens/cost BEFORE disposing, so a provider stream whose DISPOSAL throws
        // (a broken HTTP response closing) can't skip the money write. The consumer breaks after the trailing
        // usage update; disposal then throws IOException — but the 150 tokens are already recorded. Reverting
        // the finally to dispose-first loses them (Tokens would read 0).
        _provider.ThrowOnStreamDispose = true;
        var (client, meter) = Build("Managed", dailyCalls: 5);

        await Assert.ThrowsAsync<System.IO.IOException>(async () =>
        {
            var seen = 0;
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                if (++seen == 2) break; // break after the trailing usage update — disposal is what throws
            }
        });

        var today = await meter.GetTodayAsync();
        Assert.Equal(150, today.Tokens); // recorded before the throwing dispose
        Assert.Equal(1, today.Calls);
    }

    [Fact]
    public async Task A_provider_timeout_TaskCanceledException_stays_counted()
    {
        // The real SDK surfaces a timeout as TaskCanceledException (derives from OperationCanceledException),
        // so the filter must treat it as an abort that reached the provider and cost the key — stays counted,
        // never released. Pins the documented "timeout stays counted" claim against a future narrowing of
        // the filter to the exact OperationCanceledException type.
        _provider.ThrowTaskCancelled = true;
        var (client, meter) = Build("Managed", dailyCalls: 5, demoCap: 5);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AskAsync(client));

        Assert.Equal(1, (await meter.GetTodayAsync()).Calls);
        Assert.Equal(1, await DemoCallsTodayAsync());
    }

    [Fact]
    public async Task A_keyless_streaming_call_releases_the_reservation()
    {
        // F4: ByokChatClient builds the real client at request time and throws EAGERLY on a blank key,
        // before the streaming loop opens. That eager refusal (no output produced) must release the reserved
        // call like the non-streaming path does — the two paths must not disagree.
        var (client, meter) = Build("Managed", dailyCalls: 5, demoCap: 5, factoryThrows: true);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")])) { }
        });

        Assert.Equal(0, (await meter.GetTodayAsync()).Calls);
        Assert.Equal(0, await DemoCallsTodayAsync());
    }

    // ---- Balanced release: give back ONLY what was actually reserved ----

    [Fact]
    public async Task A_release_after_a_failed_reserve_does_not_touch_an_existing_count()
    {
        // F2: if the reserve WRITE silently fails but the provider then refuses, the release must give back
        // ONLY what was actually reserved. A prior call today left a row at 3; this call's reserve is faulted,
        // so the release must leave the 3 untouched — an unbalanced "always release" would DECREMENT it to 2.
        // (The no-row insert-of-a-negative case is covered by Releasing_a_household_call_with_no_row...; here
        // a row EXISTS, so only the balanced-release guard — not the negative-insert guard — can save it.) No
        // per-household cap + billing off so the gate reads nothing before the reserve, making the reserve the
        // first — and only faulted — usage write.
        await SeedTodayAsync("hh-test", calls: 3);
        _provider.ThrowRefusal = true;
        var log = new CapturingLogger<MeteredChatClient>();
        var (client, meter) = Build(
            "Managed", tier: HouseholdTier.Free, paymentsEnabled: false, meterReserveFailsFirst: true,
            clientLogger: log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(client));

        // It was the RESERVE that faulted (not the gate read) — pin that, so a future reorder that faulted the
        // gate instead (which would also read 3, gate failing open → reserve lands → 4 → release → 3) turns
        // this red rather than silently vacuous.
        Assert.Contains(log.Errors, e => e.Contains("Reserving the AI call for the household usage row failed"));
        Assert.Equal(3, (await meter.GetTodayAsync()).Calls); // unchanged — the failed reserve wasn't released
    }

    [Fact]
    public async Task A_release_after_a_failed_demo_reserve_does_not_touch_the_box_counter()
    {
        // The box-wide half of balanced release: if the DEMO reserve write fails but the provider then
        // refuses, the release must NOT decrement the box counter (an unbalanced always-release would). Seed a
        // prior box count of 3; the gate reads it (auth call #1), the reserve write faults (call #2 → boxWide
        // false), and the release leaves the 3 alone. The household reserve succeeds and is released normally.
        await SeedDemoTodayAsync(3);
        _provider.ThrowRefusal = true;
        var log = new CapturingLogger<MeteredChatClient>();
        var (client, _) = Build(
            "Managed", tier: HouseholdTier.Free, paymentsEnabled: false, demoCap: 5, demoReserveFailsOnCall: 2,
            clientLogger: log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AskAsync(client));

        // It was the demo RESERVE that faulted (auth call #2), not the gate read (#1) — pin that, so a future
        // reorder faulting the gate instead (which would also read 3) turns this red rather than vacuous.
        Assert.Contains(log.Errors, e => e.Contains("Reserving the demo box-wide call failed"));
        Assert.Equal(3, await DemoCallsTodayAsync()); // unchanged — the failed demo reserve wasn't released
    }

    [Fact]
    public async Task Releasing_a_household_call_with_no_row_for_today_writes_no_negative_row()
    {
        // The AiUsageMeter twin of the DemoUsageMeter negative-row guard: a release with no row for today (a
        // midnight-straddling release, whose reserve counted on the prior day) must not insert a "-1 calls"
        // row that raises the cap and reads "-1" on Settings/admin.
        var (_, meter) = Build("Managed");

        await meter.ReleaseLlmCallAsync(); // no reserve today — nothing to give back

        Assert.Equal(0, (await meter.GetTodayAsync()).Calls); // no row written, not -1
    }

    [Fact]
    public async Task Two_first_of_day_household_writes_racing_to_insert_dont_lose_a_count()
    {
        // The AiUsageMeter twin of the demo collision test: two requests both find no row for today and race
        // to INSERT; the loser hits the (HouseholdId, Day) unique index and must fall back to the retry-
        // increment rather than throwing and losing its count. Forced by an interceptor that inserts the
        // winning row just before ours lands. Mutating the caught error codes makes it propagate → throw.
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var interceptor = new RaceInsertInterceptor(async ct =>
        {
            var plain = new DbContextOptionsBuilder<ShelfAwareDbContext>().UseSqlite(conn).Options;
            await using var other = new ShelfAwareDbContext(plain) { HouseholdId = "hh-test" };
            other.AiUsages.Add(new AiUsage { Day = today, Calls = 5 });
            await other.SaveChangesAsync(ct);
        });
        var schemaOptions = new DbContextOptionsBuilder<ShelfAwareDbContext>().UseSqlite(conn).Options;
        using (var schema = new ShelfAwareDbContext(schemaOptions)) schema.Database.EnsureCreated();
        var meterOptions = new DbContextOptionsBuilder<ShelfAwareDbContext>()
            .UseSqlite(conn).AddInterceptors(interceptor).Options;

        var meter = new AiUsageMeter(
            new OptionsHouseholdDbFactory(meterOptions), Options.Create(new LlmOptions()),
            Options.Create(new ElevenLabsOptions()), Options.Create(new ShelfAware.Web.Billing.PaymentsOptions()),
            new FakeEntitlements(), NullLogger<AiUsageMeter>.Instance);

        await meter.ReserveLlmCallAsync(); // our +1 collides with the winner's 5 → falls back → 6, no throw

        using var read = new ShelfAwareDbContext(schemaOptions) { HouseholdId = "hh-test" };
        Assert.Equal(6, (await read.AiUsages.FirstOrDefaultAsync(u => u.Day == today))?.Calls);
    }

    private sealed class OptionsHouseholdDbFactory(DbContextOptions<ShelfAwareDbContext> options) : IHouseholdDbFactory
    {
        public Task<ShelfAwareDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ShelfAwareDbContext(options) { HouseholdId = "hh-test" });
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }

    // ---- The paid default's exact value: below it is admitted, and the operator can raise it ----

    [Fact]
    public async Task The_paid_default_cap_admits_a_call_just_below_it()
    {
        // Complement of A_paid_box_with_no_configured_call_limit... — 999 seeded is under the 1000 default,
        // so the call is allowed. Together they pin the default at exactly 1000.
        await SeedTodayAsync("hh-test", calls: 999);
        var (client, _) = Build("Managed", tier: HouseholdTier.Free, paymentsEnabled: true);

        var response = await AskAsync(client);
        Assert.Equal("ok", response.Text);
        Assert.Equal(1, _provider.Calls);
    }

    [Fact]
    public async Task An_operator_can_raise_the_call_limit_above_the_paid_default()
    {
        // Llm:DailyCallLimit WINS over the 1000 default (it's the `??` left operand), so a paid box set to
        // 2000 admits a call at 1500 seeded — a `Math.Min(explicit, 1000)` mutation would wrongly refuse it.
        await SeedTodayAsync("hh-test", calls: 1500);
        var (client, _) = Build("Managed", dailyCalls: 2000, tier: HouseholdTier.Free, paymentsEnabled: true);

        var response = await AskAsync(client);
        Assert.Equal("ok", response.Text);
        Assert.Equal(1, _provider.Calls);
    }
}
