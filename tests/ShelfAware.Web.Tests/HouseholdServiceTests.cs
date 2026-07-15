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

    /// <summary>A household plus a live code — the shape most of these tests want, and deliberately NOT
    /// what creating one gives you any more.</summary>
    private async Task<(Household Household, string Code)> HouseholdWithCodeAsync(
        HouseholdService? service = null, int? maxUses = null, string ownerEmail = "a@example.com")
    {
        service ??= _service;
        var owner = NewUser(ownerEmail);
        _context.Users.Add(owner);
        var household = await service.CreateForAsync("Home", owner);
        await service.GenerateInviteCodeAsync(household.Id, maxUses);
        return (household, household.InviteCode!);
    }

    [Fact]
    public async Task Create_assigns_the_user_and_persists_the_household()
    {
        var user = NewUser("a@example.com");
        _context.Users.Add(user);

        var household = await _service.CreateForAsync("  The Currans  ", user);

        Assert.Equal("The Currans", household.Name);
        Assert.Equal(household.Id, user.HouseholdId);

        await using var fresh = _db.CreateDbContext();
        var saved = fresh.Households.Single();
        Assert.Equal(household.Id, saved.Id);
        Assert.Equal(household.Id, fresh.Users.Single().HouseholdId);
    }

    [Fact]
    public async Task A_new_household_has_no_invite_code()
    {
        // The header act of the redesign: creation used to mint a permanent code nobody had asked for.
        var user = NewUser("a@example.com");
        _context.Users.Add(user);

        var household = await _service.CreateForAsync("Home", user);

        Assert.Null(household.InviteCode);
        Assert.False(household.InviteIsUsable(DateTimeOffset.Now));

        await using var fresh = _db.CreateDbContext();
        Assert.Null(fresh.Households.Single().InviteCode);
    }

    [Fact]
    public async Task Two_code_less_households_can_coexist()
    {
        // Why InviteCode is null and not "": the unique index counts NULLs as distinct but would let only
        // ONE household hold "". Without this, the second household created on a deployment would fail to
        // save — which is exactly the bug the sentinel would have shipped.
        var first = NewUser("a@example.com");
        var second = NewUser("b@example.com");
        _context.Users.AddRange(first, second);

        await _service.CreateForAsync("Home", first);
        await _service.CreateForAsync("Elsewhere", second);

        await using var fresh = _db.CreateDbContext();
        Assert.Equal(2, fresh.Households.Count());
        Assert.All(fresh.Households.ToList(), h => Assert.Null(h.InviteCode));
    }

    [Fact]
    public async Task Join_matches_the_code_case_insensitively_and_trims()
    {
        var (household, code) = await HouseholdWithCodeAsync();

        var joiner = NewUser("b@example.com");
        _context.Users.Add(joiner);
        var joined = await _service.JoinAsync($"  {code.ToLowerInvariant()}  ", joiner);

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
    public async Task Generating_again_invalidates_the_old_code_and_honors_the_new_one()
    {
        var (household, oldCode) = await HouseholdWithCodeAsync();

        var newCode = (await _service.GenerateInviteCodeAsync(household.Id)).InviteCode;

        Assert.NotEqual(oldCode, newCode);
        Assert.Null(await _service.FindByInviteCodeAsync(oldCode));
        Assert.NotNull(await _service.FindByInviteCodeAsync(newCode!));
    }

    [Fact]
    public async Task Generating_defaults_to_a_single_use()
    {
        // Inviting one person shouldn't hand out a key that admits a crowd — widening it is the deliberate
        // act, not narrowing it.
        var owner = NewUser("a@example.com");
        _context.Users.Add(owner);
        var household = await _service.CreateForAsync("Home", owner);

        var generated = await _service.GenerateInviteCodeAsync(household.Id);

        Assert.Equal(1, generated.InviteMaxUses);
    }

    [Fact]
    public async Task Members_lists_only_this_households_emails_sorted()
    {
        var a = NewUser("zoe@example.com");
        var b = NewUser("amy@example.com");
        var stranger = NewUser("stranger@example.com");
        _context.Users.AddRange(a, b, stranger);

        var household = await _service.CreateForAsync("Home", a);
        await _service.GenerateInviteCodeAsync(household.Id);
        await _service.JoinAsync(household.InviteCode!, b);
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
        var (household, _) = await HouseholdWithCodeAsync();

        Assert.Null(household.InviteExpiresAt);
        Assert.True(household.InviteIsUsable(DateTimeOffset.Now.AddYears(5)));
    }

    [Fact]
    public async Task A_configured_lifetime_dates_the_code()
    {
        var service = NewService(new AuthOptions { InviteCodeLifetimeDays = 7 });
        var (household, _) = await HouseholdWithCodeAsync(service);

        Assert.NotNull(household.InviteExpiresAt);
        Assert.True(household.InviteIsUsable(DateTimeOffset.Now.AddDays(6)));
        Assert.False(household.InviteIsUsable(DateTimeOffset.Now.AddDays(8)));
    }

    [Fact]
    public async Task An_expired_code_admits_nobody()
    {
        var (household, code) = await HouseholdWithCodeAsync();
        household.InviteExpiresAt = DateTimeOffset.Now.AddDays(-1);
        await _context.SaveChangesAsync();

        var joiner = NewUser("b@example.com");
        _context.Users.Add(joiner);

        Assert.Null(await _service.FindByInviteCodeAsync(code));
        Assert.Null(await _service.JoinAsync(code, joiner));
        Assert.Null(joiner.HouseholdId);
    }

    [Fact]
    public async Task Generating_again_restarts_the_clock_and_the_use_count()
    {
        // A new code is a new credential, not the old one respelled.
        var service = NewService(new AuthOptions { InviteCodeLifetimeDays = 7 });
        var (household, _) = await HouseholdWithCodeAsync(service);
        household.InviteExpiresAt = DateTimeOffset.Now.AddDays(-1);
        household.InviteUseCount = 5;
        await _context.SaveChangesAsync();

        var regenerated = await service.GenerateInviteCodeAsync(household.Id, maxUses: 1);

        Assert.Equal(0, regenerated.InviteUseCount);
        Assert.True(regenerated.InviteExpiresAt > DateTimeOffset.Now);
        Assert.True(regenerated.InviteIsUsable(DateTimeOffset.Now));
    }

    // ---- Invite use limits ----

    [Fact]
    public async Task A_single_use_code_admits_one_person_and_then_stops()
    {
        var (household, code) = await HouseholdWithCodeAsync(maxUses: 1);

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
    public async Task Spending_the_last_use_retires_the_code()
    {
        // The rule that makes a code transient rather than a fixture: once it's been used up it stops
        // existing, so the Settings page can't show a dead code that looks live, and "nobody has been
        // invited" and "somebody already came" are the same visible state — because they're the same state.
        var (household, code) = await HouseholdWithCodeAsync(maxUses: 1);

        var joiner = NewUser("b@example.com");
        _context.Users.Add(joiner);
        var joined = await _service.JoinAsync(code, joiner);

        Assert.NotNull(joined);
        Assert.Null(joined.InviteCode);

        // Not just on the returned entity — in the database.
        await using var fresh = _db.CreateDbContext();
        Assert.Null(fresh.Households.Single(h => h.Id == household.Id).InviteCode);
    }

    [Fact]
    public async Task A_multi_use_code_survives_until_its_last_use()
    {
        var (household, code) = await HouseholdWithCodeAsync(maxUses: 2);

        var first = NewUser("b@example.com");
        _context.Users.Add(first);
        var afterFirst = await _service.JoinAsync(code, first);
        Assert.NotNull(afterFirst?.InviteCode); // still live: one use left

        var second = NewUser("c@example.com");
        _context.Users.Add(second);
        var afterSecond = await _service.JoinAsync(code, second);
        Assert.NotNull(afterSecond);
        Assert.Null(afterSecond.InviteCode); // spent

        Assert.Equal(3, (await _service.GetMembersAsync(household.Id)).Count);
    }

    [Fact]
    public async Task An_unlimited_code_keeps_admitting_people_and_is_never_retired()
    {
        // maxUses null has no "last use" to spend, so the retire rule must not fire — the CASE in the
        // claim statement is guarded on InviteMaxUses being set, and a null there must mean forever.
        var (household, code) = await HouseholdWithCodeAsync(maxUses: null);

        foreach (var email in new[] { "b@example.com", "c@example.com", "d@example.com" })
        {
            var joiner = NewUser(email);
            _context.Users.Add(joiner);
            Assert.NotNull(await _service.JoinAsync(code, joiner));
        }

        Assert.Equal(4, (await _service.GetMembersAsync(household.Id)).Count);
        Assert.Equal(code, (await _service.GetAsync(household.Id))!.InviteCode);
    }

    [Fact]
    public async Task Join_returns_a_household_whose_use_count_is_current()
    {
        // The claim is an ExecuteUpdate, which writes past the change tracker — so the entity handed back
        // reported the count from before the join until it was reloaded.
        var (_, code) = await HouseholdWithCodeAsync(maxUses: 3);

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
        var (household, _) = await HouseholdWithCodeAsync(service);

        Assert.NotNull(household.InviteExpiresAt);
        Assert.False(household.InviteIsUsable(DateTimeOffset.Now.AddSeconds(1)));
    }

    [Fact]
    public async Task An_empty_code_never_matches()
    {
        // "" must not be a skeleton key into the code-less households that are now the norm.
        var user = NewUser("a@example.com");
        _context.Users.Add(user);
        await _service.CreateForAsync("Home", user);

        Assert.Null(await _service.FindByInviteCodeAsync(""));
        Assert.Null(await _service.FindByInviteCodeAsync("   "));
    }

    // ---- Clearing a code ----

    [Fact]
    public async Task Clearing_removes_the_code_and_its_limits()
    {
        var service = NewService(new AuthOptions { InviteCodeLifetimeDays = 7 });
        var (household, code) = await HouseholdWithCodeAsync(service, maxUses: 3);

        await service.ClearInviteCodeAsync(household.Id);

        await using var fresh = _db.CreateDbContext();
        var saved = fresh.Households.Single(h => h.Id == household.Id);
        Assert.Null(saved.InviteCode);
        Assert.Null(saved.InviteMaxUses);
        Assert.Null(saved.InviteExpiresAt);
        Assert.Equal(0, saved.InviteUseCount);
        Assert.Null(await service.FindByInviteCodeAsync(code));
    }

    [Fact]
    public async Task A_cleared_code_admits_nobody()
    {
        var (household, code) = await HouseholdWithCodeAsync();
        await _service.ClearInviteCodeAsync(household.Id);

        var joiner = NewUser("b@example.com");
        _context.Users.Add(joiner);

        Assert.Null(await _service.JoinAsync(code, joiner));
        Assert.Null(joiner.HouseholdId);
    }

    [Fact]
    public async Task Clearing_leaves_the_members_alone()
    {
        // Revoking the key is not evicting the people who already used it — that's RemoveMemberAsync.
        var (household, code) = await HouseholdWithCodeAsync(maxUses: null);
        var joiner = NewUser("b@example.com");
        _context.Users.Add(joiner);
        await _service.JoinAsync(code, joiner);

        await _service.ClearInviteCodeAsync(household.Id);

        Assert.Equal(2, (await _service.GetMembersAsync(household.Id)).Count);
        Assert.Equal(household.Id, joiner.HouseholdId);
    }

    [Fact]
    public async Task Clearing_a_household_with_no_code_is_a_no_op()
    {
        var user = NewUser("a@example.com");
        _context.Users.Add(user);
        var household = await _service.CreateForAsync("Home", user);

        await _service.ClearInviteCodeAsync(household.Id);
        await _service.ClearInviteCodeAsync(household.Id);

        Assert.Null(household.InviteCode);
    }

    [Fact]
    public async Task A_cleared_code_can_be_replaced_by_a_new_one()
    {
        var (household, oldCode) = await HouseholdWithCodeAsync();
        await _service.ClearInviteCodeAsync(household.Id);

        var regenerated = await _service.GenerateInviteCodeAsync(household.Id);

        Assert.NotNull(regenerated.InviteCode);
        Assert.NotEqual(oldCode, regenerated.InviteCode);
        Assert.Null(await _service.FindByInviteCodeAsync(oldCode));
        Assert.NotNull(await _service.FindByInviteCodeAsync(regenerated.InviteCode!));
    }

    // ---- Member removal ----

    private async Task<(Household Household, AppUser Owner, AppUser Other)> TwoMemberHouseholdAsync()
    {
        var owner = NewUser("a@example.com");
        var other = NewUser("b@example.com");
        _context.Users.AddRange(owner, other);
        var household = await _service.CreateForAsync("Home", owner);
        // A code has to be asked for now, and this one is spent by the join below — so anything that wants
        // to invite again has to generate a fresh one, exactly as a person would.
        await _service.GenerateInviteCodeAsync(household.Id);
        await _service.JoinAsync(household.InviteCode!, other);
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

        // Their original code was spent letting them in the first time, so inviting them back is a fresh
        // deliberate act rather than the standing code still sitting there — which is the point.
        await _service.GenerateInviteCodeAsync(household.Id);
        var rejoined = await _service.JoinAsync(household.InviteCode!, other);

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
