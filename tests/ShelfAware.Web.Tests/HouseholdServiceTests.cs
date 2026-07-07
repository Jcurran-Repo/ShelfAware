using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

public class HouseholdServiceTests : IDisposable
{
    private readonly TestAuthDb _db = new();
    private readonly AuthDbContext _context;
    private readonly HouseholdService _service;

    public HouseholdServiceTests()
    {
        _context = _db.CreateDbContext();
        _service = new HouseholdService(_context, NullLogger<HouseholdService>.Instance);
    }

    public void Dispose()
    {
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

        var newCode = await _service.RegenerateInviteCodeAsync(household.Id);

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

        var members = await _service.GetMemberEmailsAsync(household.Id);

        Assert.Equal(new[] { "amy@example.com", "zoe@example.com" }, members);
        Assert.Equal(new[] { "stranger@example.com" }, await _service.GetMemberEmailsAsync(other.Id));
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
