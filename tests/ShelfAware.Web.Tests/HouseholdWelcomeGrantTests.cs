using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>The welcome grant is seeded in CreateForAsync (the one creation choke point), atomically and
/// managed-only: a managed deployment's new household starts with the configured credit; a BYOK/self-host
/// household gets none (a host-credit row there would be meaningless).</summary>
public class HouseholdWelcomeGrantTests : IDisposable
{
    private readonly TestAuthDb _authDb = new();

    public void Dispose() => _authDb.Dispose();

    private (HouseholdService Service, AuthDbContext Db) Build(bool managed)
    {
        var db = _authDb.CreateDbContext();
        var users = new UserManager<AppUser>(
            new UserStore<AppUser>(db), Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(), [], [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<AppUser>>.Instance);
        var llm = new LlmOptions { KeyMode = managed ? "Managed" : "Byok" };
        var service = new HouseholdService(db, users, Options.Create(new AuthOptions()),
            Options.Create(llm), Options.Create(new BillingOptions()), NullLogger<HouseholdService>.Instance);
        return (service, db);
    }

    /// <summary>A persisted user on the service's own context — the state CreateForAsync's slot-claim
    /// needs (every production caller writes the user row before we're reached).</summary>
    private static async Task<AppUser> PersistedUserAsync(AuthDbContext db, string email)
    {
        var user = new AppUser { UserName = email, Email = email };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task A_managed_deployment_seeds_the_welcome_grant_atomically_with_creation()
    {
        var (service, db) = Build(managed: true);
        var user = await PersistedUserAsync(db, "a@x.com");
        var household = await service.CreateForAsync("Home", user);

        await using var read = _authDb.CreateDbContext();
        var grant = Assert.Single(await read.CreditLedger.Where(e => e.HouseholdId == household.Id).ToListAsync());
        Assert.Equal(CreditEntryKind.Grant, grant.Kind);
        Assert.Equal(AiPricing.WelcomeGrantRetailMicros(new BillingOptions()), grant.AmountMicros); // $1 × 1.65
    }

    [Fact]
    public async Task A_byok_deployment_gives_no_welcome_grant()
    {
        var (service, db) = Build(managed: false);
        var user = await PersistedUserAsync(db, "a@x.com");
        var household = await service.CreateForAsync("Home", user);

        await using var read = _authDb.CreateDbContext();
        Assert.Empty(await read.CreditLedger.Where(e => e.HouseholdId == household.Id).ToListAsync());
    }
}
