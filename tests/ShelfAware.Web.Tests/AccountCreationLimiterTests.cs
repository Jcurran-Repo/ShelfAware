using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The demo box's daily account-creation cap (§10): count today's <see cref="AppUser.CreatedOn"/> rows and
/// compare to the configured limit. Real EF on in-memory SQLite (like the rest of this suite), so the query
/// — nullable-DateOnly equality included — behaves exactly as production.
/// </summary>
public sealed class AccountCreationLimiterTests : IDisposable
{
    private readonly TestAuthDb _db = new();
    private readonly AuthDbContext _context;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    public AccountCreationLimiterTests()
    {
        _context = _db.CreateDbContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private AccountCreationLimiter Limiter(int? limit) =>
        new(_context, Options.Create(new AuthOptions { DailyAccountCreationLimit = limit }));

    private async Task SeedAccountsAsync(int count, DateOnly? createdOn)
    {
        for (var i = 0; i < count; i++)
        {
            var email = $"{Guid.NewGuid():n}@example.test";
            _context.Users.Add(new AppUser { UserName = email, Email = email, CreatedOn = createdOn });
        }
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task No_configured_limit_is_never_at_the_cap()
    {
        await SeedAccountsAsync(50, Today); // well past any plausible cap

        // Null limit = the self-host/family default: the limiter short-circuits without even counting.
        Assert.False(await Limiter(null).AtDailyLimitAsync());
    }

    [Fact]
    public async Task Under_the_limit_is_not_at_the_cap()
    {
        await SeedAccountsAsync(2, Today);

        Assert.False(await Limiter(3).AtDailyLimitAsync());
    }

    [Fact]
    public async Task At_the_limit_is_at_the_cap()
    {
        // The == case — what tells "count >= limit" apart from "count > limit".
        await SeedAccountsAsync(3, Today);

        Assert.True(await Limiter(3).AtDailyLimitAsync());
    }

    [Fact]
    public async Task Only_todays_accounts_count_toward_the_cap()
    {
        await SeedAccountsAsync(1, Today);
        await SeedAccountsAsync(5, Today.AddDays(-1)); // yesterday's accounts don't count against today

        // One today, limit two → not at the cap: the five yesterday rows are excluded.
        Assert.False(await Limiter(2).AtDailyLimitAsync());
    }

    [Fact]
    public async Task Accounts_with_no_creation_date_do_not_count()
    {
        // Pre-feature accounts land on NULL CreatedOn; NULL never equals "today", so they can't inflate
        // the cap (and can't lock a demo box out at boot on day one).
        await SeedAccountsAsync(5, createdOn: null);

        Assert.False(await Limiter(1).AtDailyLimitAsync());
    }
}
