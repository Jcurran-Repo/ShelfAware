using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The managed-mode pre-check composite (<see cref="ManagedCallCaps"/>): the household's own daily
/// allowance is asked first, then the box-wide demo valve — the order the server-side gate checks them —
/// and each answers with the same sentence its throwing twin throws. Until 2026-10-07 only the valve was
/// mirrored, so a household at its own cap was shown the surface's generic "try again".
/// </summary>
public class ManagedCallCapsTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly DbContextOptions<ShelfAwareDbContext> _options;

    public ManagedCallCapsTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<ShelfAwareDbContext>().UseSqlite(_conn).Options;
        using var schema = new ShelfAwareDbContext(_options);
        schema.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private AiUsageMeter Household(int? dailyCallLimit, HouseholdTier tier = HouseholdTier.Free) =>
        new(new HouseholdFactory(_options),
            Options.Create(new LlmOptions { KeyMode = "managed", ApiKey = "sk-host", DailyCallLimit = dailyCallLimit }),
            Options.Create(new ElevenLabsOptions()),
            Options.Create(new ShelfAware.Web.Billing.PaymentsOptions()),
            new FakeEntitlements(tier),
            NullLogger<AiUsageMeter>.Instance);

    /// <summary>A box-wide valve with no cap configured never reads its database — the unconfigured
    /// family / self-host shape — so a throwing auth factory proves it stayed out of the way.</summary>
    private static DemoUsageMeter NoBoxWideCap() =>
        new(new ThrowingAuthDbFactory(), Options.Create(new DemoOptions()), NullLogger<DemoUsageMeter>.Instance);

    [Fact]
    public async Task No_caps_configured_is_not_blocked()
    {
        var caps = new ManagedCallCaps(Household(dailyCallLimit: null), NoBoxWideCap());

        Assert.Null(await caps.CallBlockedMessageAsync());
    }

    [Fact]
    public async Task A_household_at_its_own_daily_cap_is_told_so_in_the_gates_words()
    {
        // A cap of 0 is at cap from the first call (the kill-switch rule the gate holds), with no usage row.
        var caps = new ManagedCallCaps(Household(dailyCallLimit: 0), NoBoxWideCap());

        var reason = await caps.CallBlockedMessageAsync();

        Assert.Equal(AiUsageMeter.DailyAllowanceUsedUp, reason);
        // ⚠️ The old sentence told a managed household to "bring your own key in Settings" — a panel that
        // box hides. The replacement must never point at it.
        Assert.DoesNotContain("your own key", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_pre_check_and_the_throwing_gate_say_the_same_thing()
    {
        var meter = Household(dailyCallLimit: 0);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => meter.EnsureLlmCallAllowedAsync());

        Assert.Equal(await meter.DailyCapBlockedMessageAsync(), thrown.Message);
    }

    [Fact]
    public async Task A_founder_household_is_exempt_from_its_daily_cap()
    {
        var caps = new ManagedCallCaps(Household(dailyCallLimit: 0, HouseholdTier.Founder), NoBoxWideCap());

        Assert.Null(await caps.CallBlockedMessageAsync());
    }

    private sealed class HouseholdFactory(DbContextOptions<ShelfAwareDbContext> options) : IHouseholdDbFactory
    {
        public Task<ShelfAwareDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ShelfAwareDbContext(options) { HouseholdId = "hh-test" });
    }

    private sealed class ThrowingAuthDbFactory : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => throw new InvalidOperationException("auth.db must not be read");
        public Task<AuthDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("auth.db must not be read");
    }
}
