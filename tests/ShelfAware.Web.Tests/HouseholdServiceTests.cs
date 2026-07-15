using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

public class HouseholdServiceTests : IDisposable
{
    private readonly TestAuthDb _db = new();
    private readonly AuthDbContext _context;
    private readonly UserManager<AppUser> _users;
    private readonly HouseholdService _service;

    public HouseholdServiceTests()
    {
        _context = _db.CreateDbContext();
        _users = NewUserManager(_context);
        _service = NewService(new AuthOptions());
    }

    private HouseholdService NewService(AuthOptions options) =>
        new(_context, _users, Options.Create(options), NullLogger<HouseholdService>.Instance);

    /// <summary>A REAL UserManager over the test context, not a fake: removing a member is only a removal
    /// because it bumps the security stamp, and a fake would let that go untested.</summary>
    private static UserManager<AppUser> NewUserManager(AuthDbContext context) => new(
        new UserStore<AppUser>(context),
        Options.Create(new IdentityOptions()),
        new PasswordHasher<AppUser>(),
        [],
        [],
        new UpperInvariantLookupNormalizer(),
        new IdentityErrorDescriber(),
        null!,
        NullLogger<UserManager<AppUser>>.Instance);

    public void Dispose()
    {
        _users.Dispose();
        _context.Dispose();
        _db.Dispose();
    }

    private static AppUser NewUser(string email) => new()
    {
        UserName = email,
        Email = email,
    };

    // ---- Invite codes ----

    [Fact]
    public void Invite_codes_use_the_unambiguous_alphabet_at_full_length()
    {
        for (var i = 0; i < 50; i++)
        {
            var code = HouseholdService.NewInviteCode();
            Assert.Equal(HouseholdService.InviteCodeLength, code.Length);
            Assert.All(code, c => Assert.Contains(c, "ABCDEFGHJKMNPQRSTUVWXYZ23456789"));
        }
    }

    [Fact]
    public void Invite_codes_are_effectively_unique()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => HouseholdService.NewInviteCode()).ToHashSet();
        Assert.Equal(500, codes.Count);
    }

    // ---- Registration gate (create-a-new-household path) ----

    [Theory]
    [InlineData(true, false, true)]   // open registration, fresh deploy
    [InlineData(true, true, true)]    // open registration, users exist
    [InlineData(false, false, true)]  // locked down, but FIRST user bootstraps
    [InlineData(false, true, false)]  // locked down, users exist → closed
    public void MayCreateHousehold_matrix(bool allowRegistration, bool anyUsersExist, bool expected)
        => Assert.Equal(expected, HouseholdService.MayCreateHousehold(allowRegistration, anyUsersExist));

    // ---- Create / join ----

    [Fact]
    public async Task Create_assigns_the_user_and_persists_the_household()
    {
        var user = NewUser("a@example.com");
        _context.Users.Add(user);

        var household = await _service.CreateForAsync("  The Currans  ", user);

        Assert.Equal("The Currans", household.Name);
        Assert.Equal(household.Id, user.HouseholdId);
        Assert.Equal(HouseholdService.InviteCodeLength, household.InviteCode.Length);

        await using var fresh = _db.CreateDbContext();
        var saved = fresh.Households.Single();
        Assert.Equal(household.Id, saved.Id);
        Assert.Equal(household.Id, fresh.Users.Single().HouseholdId);
    }

    [Fact]
    public async Task Join_matches_the_code_case_insensitively_and_trims()
    {
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);
        var household = await _service.CreateForAsync("Home", owner);

        var joiner = NewUser("b@example.com");
        _context.Users.Add(joiner);
        var joined = await _service.JoinAsync($"  {household.InviteCode.ToLowerInvariant()}  ", joiner);

        Assert.NotNull(joined);
        Assert.Equal(household.Id, joiner.HouseholdId);
    }

    [Fact]
    public async Task Join_with_an_unknown_code_returns_null_and_assigns_nothing()
    {
        var user = NewUser("a@example.com");
        _context.Users.Add(user);

        var joined = await _service.JoinAsync("NOPENOPE22", user);

        Assert.Null(joined);
        Assert.Null(user.HouseholdId);
    }

    [Fact]
    public async Task Regenerate_invalidates_the_old_code_and_honors_the_new_one()
    {
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);
        var household = await _service.CreateForAsync("Home", owner);
        var oldCode = household.InviteCode;

        var newCode = (await _service.RegenerateInviteCodeAsync(household.Id)).InviteCode;

        Assert.NotEqual(oldCode, newCode);
        Assert.Null(await _service.FindByInviteCodeAsync(oldCode));
        Assert.NotNull(await _service.FindByInviteCodeAsync(newCode));
    }

    [Fact]
    public async Task Members_lists_only_this_households_emails_sorted()
    {
        var a = NewUser("zoe@example.com");
        var b = NewUser("amy@example.com");
        var stranger = NewUser("stranger@example.com");
        _context.Users.AddRange(a, b, stranger);

        var household = await _service.CreateForAsync("Home", a);
        await _service.JoinAsync(household.InviteCode, b);
        var other = await _service.CreateForAsync("Elsewhere", stranger);

        var members = await _service.GetMembersAsync(household.Id);

        Assert.Equal(["amy@example.com", "zoe@example.com"], members.Select(m => m.Email));
        Assert.Equal(["stranger@example.com"], (await _service.GetMembersAsync(other.Id)).Select(m => m.Email));
    }

    // ---- Invite expiry ----

    [Fact]
    public async Task Without_a_configured_lifetime_a_code_never_expires()
    {
        // The self-host default, and what every code minted before expiry existed already is.
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);

        var household = await _service.CreateForAsync("Home", owner);

        Assert.Null(household.InviteExpiresAt);
        Assert.True(household.InviteIsUsable(DateTimeOffset.Now.AddYears(5)));
    }

    [Fact]
    public async Task A_configured_lifetime_dates_the_code()
    {
        var service = NewService(new AuthOptions { InviteCodeLifetimeDays = 7 });
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);

        var household = await service.CreateForAsync("Home", owner);

        Assert.NotNull(household.InviteExpiresAt);
        Assert.True(household.InviteIsUsable(DateTimeOffset.Now.AddDays(6)));
        Assert.False(household.InviteIsUsable(DateTimeOffset.Now.AddDays(8)));
    }

    [Fact]
    public async Task An_expired_code_admits_nobody()
    {
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);
        var household = await _service.CreateForAsync("Home", owner);
        household.InviteExpiresAt = DateTimeOffset.Now.AddDays(-1);
        await _context.SaveChangesAsync();

        var joiner = NewUser("b@example.com");
        _context.Users.Add(joiner);

        Assert.Null(await _service.FindByInviteCodeAsync(household.InviteCode));
        Assert.Null(await _service.JoinAsync(household.InviteCode, joiner));
        Assert.Null(joiner.HouseholdId);
    }

    [Fact]
    public async Task Regenerating_restarts_the_clock_and_the_use_count()
    {
        // A regenerated code is a new credential, not the old one respelled.
        var service = NewService(new AuthOptions { InviteCodeLifetimeDays = 7 });
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);
        var household = await service.CreateForAsync("Home", owner);
        household.InviteExpiresAt = DateTimeOffset.Now.AddDays(-1);
        household.InviteUseCount = 5;
        await _context.SaveChangesAsync();

        var regenerated = await service.RegenerateInviteCodeAsync(household.Id, maxUses: 1);

        Assert.Equal(0, regenerated.InviteUseCount);
        Assert.True(regenerated.InviteExpiresAt > DateTimeOffset.Now);
        Assert.True(regenerated.InviteIsUsable(DateTimeOffset.Now));
    }

    // ---- Invite use limits ----

    [Fact]
    public async Task A_single_use_code_admits_one_person_and_then_stops()
    {
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);
        var household = await _service.CreateForAsync("Home", owner);
        await _service.RegenerateInviteCodeAsync(household.Id, maxUses: 1);
        var code = (await _service.GetAsync(household.Id))!.InviteCode;

        var first = NewUser("b@example.com");
        _context.Users.Add(first);
        Assert.NotNull(await _service.JoinAsync(code, first));
        Assert.Equal(household.Id, first.HouseholdId);

        var second = NewUser("c@example.com");
        _context.Users.Add(second);
        Assert.Null(await _service.JoinAsync(code, second));
        Assert.Null(second.HouseholdId);
    }

    [Fact]
    public async Task An_unlimited_code_keeps_admitting_people()
    {
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);
        var household = await _service.CreateForAsync("Home", owner);

        foreach (var email in new[] { "b@example.com", "c@example.com", "d@example.com" })
        {
            var joiner = NewUser(email);
            _context.Users.Add(joiner);
            Assert.NotNull(await _service.JoinAsync(household.InviteCode, joiner));
        }

        Assert.Equal(4, (await _service.GetMembersAsync(household.Id)).Count);
    }

    [Fact]
    public async Task Join_returns_a_household_whose_use_count_is_current()
    {
        // The claim is an ExecuteUpdate, which writes past the change tracker — so the entity handed back
        // reported the count from before the join until it was reloaded.
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);
        var household = await _service.CreateForAsync("Home", owner);
        await _service.RegenerateInviteCodeAsync(household.Id, maxUses: 3);
        var code = (await _service.GetAsync(household.Id))!.InviteCode;

        var joiner = NewUser("b@example.com");
        _context.Users.Add(joiner);
        var joined = await _service.JoinAsync(code, joiner);

        Assert.NotNull(joined);
        Assert.Equal(1, joined.InviteUseCount);
        Assert.Equal(2, joined.InviteUsesRemaining);
    }

    [Fact]
    public async Task A_zero_day_lifetime_would_expire_immediately_rather_than_never()
    {
        // Startup validation refuses 0 outright (Program.cs), so this can't be configured — but if one
        // ever slipped through, it must fail CLOSED. It used to be read as "never expires".
        var service = NewService(new AuthOptions { InviteCodeLifetimeDays = 0 });
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);

        var household = await service.CreateForAsync("Home", owner);

        Assert.NotNull(household.InviteExpiresAt);
        Assert.False(household.InviteIsUsable(DateTimeOffset.Now.AddSeconds(1)));
    }

    [Fact]
    public async Task An_empty_code_never_matches()
    {
        // Guards the degenerate case where a household somehow has no code: "" must not be a skeleton key.
        Assert.Null(await _service.FindByInviteCodeAsync(""));
        Assert.Null(await _service.FindByInviteCodeAsync("   "));
    }

    // ---- Member removal ----

    private async Task<(Household Household, AppUser Owner, AppUser Other)> TwoMemberHouseholdAsync()
    {
        var owner = NewUser("a@example.com");
        var other = NewUser("b@example.com");
        _context.Users.AddRange(owner, other);
        var household = await _service.CreateForAsync("Home", owner);
        await _service.JoinAsync(household.InviteCode, other);
        await _context.SaveChangesAsync();
        return (household, owner, other);
    }

    [Fact]
    public async Task Removing_a_member_clears_their_household_and_bumps_their_security_stamp()
    {
        var (household, owner, other) = await TwoMemberHouseholdAsync();
        var stampBefore = other.SecurityStamp;

        var refused = await _service.RemoveMemberAsync(household.Id, other.Id, actingUserId: owner.Id);

        Assert.Null(refused);
        Assert.Null(other.HouseholdId);
        // The household id rides in the auth COOKIE. Without a new stamp their existing cookie would keep
        // asserting membership until it happened to be re-issued — this is what actually removes them.
        Assert.NotEqual(stampBefore, other.SecurityStamp);
        Assert.Equal(["a@example.com"], (await _service.GetMembersAsync(household.Id)).Select(m => m.Email));
    }

    [Fact]
    public async Task You_cannot_remove_yourself()
    {
        var (household, owner, _) = await TwoMemberHouseholdAsync();

        var refused = await _service.RemoveMemberAsync(household.Id, owner.Id, actingUserId: owner.Id);

        Assert.NotNull(refused);
        Assert.Equal(household.Id, owner.HouseholdId);
    }

    [Fact]
    public async Task An_outsider_cannot_remove_a_member()
    {
        // The method is the authorization boundary, not Settings.razor. It checks the ACTOR belongs here,
        // rather than trusting its one caller to have derived the id from the right claim.
        var (household, _, other) = await TwoMemberHouseholdAsync();
        var outsider = NewUser("z@example.com");
        _context.Users.Add(outsider);
        await _service.CreateForAsync("Elsewhere", outsider);
        await _context.SaveChangesAsync();

        var refused = await _service.RemoveMemberAsync(household.Id, other.Id, actingUserId: outsider.Id);

        Assert.NotNull(refused);
        Assert.Equal(household.Id, other.HouseholdId);
    }

    [Fact]
    public async Task A_household_can_never_be_emptied()
    {
        // The pantry belongs to the household, so a household with nobody in it is data nobody can read,
        // export, or delete. No rule enforces this directly — it falls out of "the actor must be a member"
        // plus "you can't remove yourself", which together mean there are always at least two people and
        // the remover is always one of the survivors. This pins the OUTCOME, whatever the mechanism.
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);
        var household = await _service.CreateForAsync("Home", owner);
        await _context.SaveChangesAsync();

        // The only member trying to remove the only member: refused, so the household keeps its last one.
        var refused = await _service.RemoveMemberAsync(household.Id, owner.Id, actingUserId: owner.Id);

        Assert.NotNull(refused);
        Assert.Equal(household.Id, owner.HouseholdId);
        Assert.Single(await _service.GetMembersAsync(household.Id));
    }

    [Fact]
    public async Task You_cannot_remove_someone_from_a_household_they_are_not_in()
    {
        var (household, owner, _) = await TwoMemberHouseholdAsync();
        var stranger = NewUser("stranger@example.com");
        _context.Users.Add(stranger);
        await _service.CreateForAsync("Elsewhere", stranger);
        await _context.SaveChangesAsync();

        var refused = await _service.RemoveMemberAsync(household.Id, stranger.Id, actingUserId: owner.Id);

        Assert.NotNull(refused);
        Assert.NotNull(stranger.HouseholdId);
    }

    [Fact]
    public async Task A_removed_member_can_be_invited_back()
    {
        var (household, owner, other) = await TwoMemberHouseholdAsync();
        await _service.RemoveMemberAsync(household.Id, other.Id, actingUserId: owner.Id);

        var rejoined = await _service.JoinAsync(household.InviteCode, other);

        Assert.NotNull(rejoined);
        Assert.Equal(household.Id, other.HouseholdId);
    }

    [Fact]
    public async Task Rename_updates_the_household()
    {
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);
        var household = await _service.CreateForAsync("Home", owner);

        await _service.RenameAsync(household.Id, " New Name ");

        await using var fresh = _db.CreateDbContext();
        Assert.Equal("New Name", fresh.Households.Single().Name);
    }

    [Fact]
    public async Task AnyUsers_reflects_the_store()
    {
        Assert.False(await _service.AnyUsersAsync());
        _context.Users.Add(NewUser("a@example.com"));
        await _context.SaveChangesAsync();
        Assert.True(await _service.AnyUsersAsync());
    }
}
