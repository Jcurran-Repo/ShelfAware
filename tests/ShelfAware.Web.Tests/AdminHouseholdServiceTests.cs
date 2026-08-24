using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>The admin household roster + the Founder grant. auth.db has no tenancy query filter, so the
/// cross-household REACH the report services need (IgnoreQueryFilters) isn't in play here; what these
/// pin is the admin gate on both halves, that a grant sets exactly Tier + FounderSince on exactly the
/// named household, that re-granting keeps the original date (the COALESCE), and that revoking clears
/// both.</summary>
public class AdminHouseholdServiceTests : IDisposable
{
    private const string AdminEmail = "jordan@example.com";

    private readonly TestAuthDb _authDb = new();

    public void Dispose() => _authDb.Dispose();

    private sealed class FakeAuthState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private static ClaimsPrincipal SignedIn(string email) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, email)], authenticationType: "test"));

    private AdminHouseholdService Service(string signedInAs = AdminEmail) => new(
        _authDb,
        new FakeAuthState(SignedIn(signedInAs)),
        Options.Create(new AdminOptions { Emails = [AdminEmail] }));

    private async Task<string> SeedHouseholdAsync(string name, HouseholdTier tier = HouseholdTier.Free)
    {
        await using var db = _authDb.CreateDbContext();
        var household = new Household { Name = name, Tier = tier };
        db.Households.Add(household);
        await db.SaveChangesAsync();
        return household.Id;
    }

    private async Task<Household> ReadAsync(string id)
    {
        await using var db = _authDb.CreateDbContext();
        return await db.Households.AsNoTracking().SingleAsync(h => h.Id == id);
    }

    [Fact]
    public async Task The_roster_lists_households_with_members_and_tier()
    {
        await using (var db = _authDb.CreateDbContext())
        {
            var curransHh = new Household { Name = "The Currans", Tier = HouseholdTier.Founder };
            var neighboursHh = new Household { Name = "The Neighbours", Tier = HouseholdTier.Free };
            db.Households.AddRange(curransHh, neighboursHh);
            db.Users.Add(new AppUser { UserName = "jordan@example.com", Email = "jordan@example.com", HouseholdId = curransHh.Id });
            db.Users.Add(new AppUser { UserName = "wife@example.com", Email = "wife@example.com", HouseholdId = curransHh.Id });
            db.Users.Add(new AppUser { UserName = "next@example.com", Email = "next@example.com", HouseholdId = neighboursHh.Id });
            await db.SaveChangesAsync();
        }

        var roster = await Service().ListAsync();

        Assert.Equal(2, roster.Count);
        var currans = roster.Single(h => h.Name == "The Currans");
        Assert.Equal(HouseholdTier.Founder, currans.Tier);
        Assert.Contains("jordan@example.com", currans.MemberEmails);
        Assert.Contains("wife@example.com", currans.MemberEmails);
        var neighbours = roster.Single(h => h.Name == "The Neighbours");
        Assert.Equal(HouseholdTier.Free, neighbours.Tier);
        Assert.Equal(["next@example.com"], neighbours.MemberEmails);
    }

    [Fact]
    public async Task Granting_founder_sets_the_tier_and_stamps_the_date()
    {
        var id = await SeedHouseholdAsync("The Currans");

        Assert.True(await Service().SetFounderAsync(id, true));

        var h = await ReadAsync(id);
        Assert.Equal(HouseholdTier.Founder, h.Tier);
        Assert.NotNull(h.FounderSince);
    }

    [Fact]
    public async Task Re_granting_keeps_the_original_founder_date()
    {
        var id = await SeedHouseholdAsync("The Currans");
        await Service().SetFounderAsync(id, true);
        var firstDate = (await ReadAsync(id)).FounderSince;

        await Service().SetFounderAsync(id, true); // grant again — must not re-stamp

        Assert.Equal(firstDate, (await ReadAsync(id)).FounderSince);
    }

    [Fact]
    public async Task Removing_founder_reverts_to_free_and_clears_the_date()
    {
        var id = await SeedHouseholdAsync("The Currans");
        await Service().SetFounderAsync(id, true); // grant first, so there's a date to clear

        Assert.True(await Service().SetFounderAsync(id, false));

        var h = await ReadAsync(id);
        Assert.Equal(HouseholdTier.Free, h.Tier);
        Assert.Null(h.FounderSince);
    }

    [Fact]
    public async Task A_grant_touches_only_the_named_household()
    {
        var target = await SeedHouseholdAsync("The Currans");
        var other = await SeedHouseholdAsync("The Neighbours");

        await Service().SetFounderAsync(target, true);

        Assert.Equal(HouseholdTier.Founder, (await ReadAsync(target)).Tier);
        Assert.Equal(HouseholdTier.Free, (await ReadAsync(other)).Tier);
    }

    [Fact]
    public async Task A_household_that_no_longer_exists_answers_false()
    {
        // Deleted between the roster render and the click, say.
        Assert.False(await Service().SetFounderAsync("does-not-exist", true));
    }

    [Fact]
    public async Task Anyone_but_the_configured_admin_is_refused_by_both_halves()
    {
        var id = await SeedHouseholdAsync("The Currans");
        var intruder = Service(signedInAs: "wife@example.com");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => intruder.ListAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => intruder.SetFounderAsync(id, true));

        // Refused BEFORE any data was touched.
        Assert.Equal(HouseholdTier.Free, (await ReadAsync(id)).Tier);
    }
}
