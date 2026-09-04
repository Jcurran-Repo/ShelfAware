using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Superseding an unconfirmed placeholder on re-registration (the pre-hijack partial fix). Real EF on
/// in-memory SQLite, so the deletes and the household/ledger cleanup behave exactly as production. Pins
/// that a pending placeholder (and its now-empty household) is removed, an invite-joined one leaves its
/// populated household alone, and a real (confirmed) account is never touched.
/// </summary>
public sealed class AccountSupersessionTests : IDisposable
{
    private readonly TestAuthDb _db = new();
    private readonly AuthDbContext _ctx;
    private readonly UserManager<AppUser> _users;
    private readonly AccountSupersession _sut;

    public AccountSupersessionTests()
    {
        _ctx = _db.CreateDbContext();
        _users = new UserManager<AppUser>(
            new UserStore<AppUser>(_ctx), Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(), [], [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<AppUser>>.Instance);
        _sut = new AccountSupersession(_ctx, _users, NullLogger<AccountSupersession>.Instance);
    }

    public void Dispose()
    {
        _users.Dispose();
        _ctx.Dispose();
        _db.Dispose();
    }

    private async Task<string> SeedUnconfirmedPlaceholderAsync(string email, string householdId)
    {
        var placeholder = new AppUser { UserName = email, Email = email, HouseholdId = householdId };
        Assert.True((await _users.CreateAsync(placeholder)).Succeeded); // unconfirmed by default
        return placeholder.Id;
    }

    private async Task<string> SeedHouseholdAsync()
    {
        var household = new Household { Name = "Placeholder HH" };
        _ctx.Households.Add(household);
        await _ctx.SaveChangesAsync();
        return household.Id;
    }

    [Fact]
    public async Task An_unconfirmed_sole_member_placeholder_and_its_empty_household_and_grant_are_removed()
    {
        var hid = await SeedHouseholdAsync();
        await SeedUnconfirmedPlaceholderAsync("victim@x.test", hid);
        _ctx.CreditLedger.Add(new CreditLedgerEntry
        {
            HouseholdId = hid, Kind = CreditEntryKind.Grant, AmountMicros = 1_650_000, Reason = "Welcome grant",
        });
        await _ctx.SaveChangesAsync();

        await _sut.SupersedeUnconfirmedPlaceholderAsync("victim@x.test");

        Assert.Null(await _users.FindByEmailAsync("victim@x.test"));                 // placeholder gone
        Assert.False(await _ctx.Households.AnyAsync(h => h.Id == hid));              // empty household gone
        Assert.False(await _ctx.CreditLedger.AnyAsync(e => e.HouseholdId == hid));  // its grant gone too
    }

    [Fact]
    public async Task An_invite_joined_placeholder_is_removed_but_its_populated_household_is_kept()
    {
        // The hijack shape: the attacker's household already has the attacker in it, and the victim was
        // pre-registered (unconfirmed) INTO it. Superseding removes the victim's placeholder but must NOT
        // delete the household — it still belongs to the attacker (and any real members).
        var hid = await SeedHouseholdAsync();
        var attacker = new AppUser { UserName = "attacker@x.test", Email = "attacker@x.test", HouseholdId = hid, EmailConfirmed = true };
        Assert.True((await _users.CreateAsync(attacker)).Succeeded);
        await SeedUnconfirmedPlaceholderAsync("victim@x.test", hid);

        await _sut.SupersedeUnconfirmedPlaceholderAsync("victim@x.test");

        Assert.Null(await _users.FindByEmailAsync("victim@x.test"));      // the pre-registered placeholder gone
        Assert.True(await _ctx.Households.AnyAsync(h => h.Id == hid));    // household kept — still has the attacker
        Assert.NotNull(await _users.FindByEmailAsync("attacker@x.test")); // and the other member is untouched
    }

    [Fact]
    public async Task A_confirmed_account_is_left_untouched()
    {
        var hid = await SeedHouseholdAsync();
        await SeedUnconfirmedPlaceholderAsync("real@x.test", hid);
        var u = await _users.FindByEmailAsync("real@x.test");
        u!.EmailConfirmed = true;
        await _users.UpdateAsync(u);

        await _sut.SupersedeUnconfirmedPlaceholderAsync("real@x.test");

        Assert.NotNull(await _users.FindByEmailAsync("real@x.test"));  // a real account is never superseded
        Assert.True(await _ctx.Households.AnyAsync(h => h.Id == hid));
    }

    [Fact]
    public async Task A_missing_email_is_a_no_op()
    {
        var ex = await Record.ExceptionAsync(() => _sut.SupersedeUnconfirmedPlaceholderAsync("nobody@x.test"));
        Assert.Null(ex);
    }
}
