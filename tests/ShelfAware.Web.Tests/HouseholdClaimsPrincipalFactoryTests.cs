using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>The household claim is how EVERY request resolves its tenant without a DB round-trip —
/// the id rides in the auth cookie. A missing claim must mean "no household" (the /Account/Household
/// middleware handles it), never an empty-string claim some parser might read as a real tenant.</summary>
public sealed class HouseholdClaimsPrincipalFactoryTests : IDisposable
{
    private readonly TestAuthDb _db = new();
    private readonly AuthDbContext _context;
    private readonly UserManager<AppUser> _users;
    private readonly HouseholdClaimsPrincipalFactory _factory;

    public HouseholdClaimsPrincipalFactoryTests()
    {
        _context = _db.CreateDbContext();
        _users = new UserManager<AppUser>(
            new UserStore<AppUser>(_context), Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(), [], [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<AppUser>>.Instance);
        _factory = new HouseholdClaimsPrincipalFactory(_users, Options.Create(new IdentityOptions()));
    }

    public void Dispose()
    {
        _users.Dispose();
        _context.Dispose();
        _db.Dispose();
    }

    private async Task<AppUser> SeedUser(string? householdId)
    {
        var user = new AppUser { UserName = "a@example.com", Email = "a@example.com", HouseholdId = householdId };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task A_member_gets_the_household_claim_in_their_principal()
    {
        var principal = await _factory.CreateAsync(await SeedUser("hh-home"));

        Assert.Equal("hh-home", principal.FindFirst(HouseholdClaimsPrincipalFactory.HouseholdClaim)?.Value);
    }

    [Fact]
    public async Task A_user_with_no_household_gets_no_claim_at_all()
    {
        var principal = await _factory.CreateAsync(await SeedUser(householdId: null));

        Assert.Null(principal.FindFirst(HouseholdClaimsPrincipalFactory.HouseholdClaim));
    }
}
