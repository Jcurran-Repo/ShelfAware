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

    private HouseholdService Service(bool managed)
    {
        var db = _authDb.CreateDbContext();
        var users = new UserManager<AppUser>(
            new UserStore<AppUser>(db), Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(), [], [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<AppUser>>.Instance);
        var llm = new LlmOptions { KeyMode = managed ? "Managed" : "Byok" };
        return new HouseholdService(db, users, Options.Create(new AuthOptions()),
            Options.Create(llm), Options.Create(new BillingOptions()), NullLogger<HouseholdService>.Instance);
    }

    [Fact]
    public async Task A_managed_deployment_seeds_the_welcome_grant_atomically_with_creation()
    {
        var household = await Service(managed: true)
            .CreateForAsync("Home", new AppUser { UserName = "a@x.com", Email = "a@x.com" });

        await using var db = _authDb.CreateDbContext();
        var grant = Assert.Single(await db.CreditLedger.Where(e => e.HouseholdId == household.Id).ToListAsync());
        Assert.Equal(CreditEntryKind.Grant, grant.Kind);
        Assert.Equal(AiPricing.WelcomeGrantRetailMicros(new BillingOptions()), grant.AmountMicros); // $1 × 1.65
    }

    [Fact]
    public async Task A_byok_deployment_gives_no_welcome_grant()
    {
        var household = await Service(managed: false)
            .CreateForAsync("Home", new AppUser { UserName = "a@x.com", Email = "a@x.com" });

        await using var db = _authDb.CreateDbContext();
        Assert.Empty(await db.CreditLedger.Where(e => e.HouseholdId == household.Id).ToListAsync());
    }
}
