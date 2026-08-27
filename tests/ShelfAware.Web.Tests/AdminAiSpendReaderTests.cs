using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>The admin dashboard's cross-household AI spend — the app's third IgnoreQueryFilters. Two
/// things are load-bearing and pinned here: the gate (everyone but the admin is refused before any read),
/// and the cross-household sum (the whole reason for the IgnoreQueryFilters — a household-scoped read
/// would see only the caller's own row). The today/month windowing is deterministic and lives in
/// AiSpendRollupTests; this suite proves the access.</summary>
public class AdminAiSpendReaderTests : IDisposable
{
    private const string AdminEmail = "jordan@example.com";
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class FakeAuthState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private static ClaimsPrincipal SignedIn(string email) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, email)], authenticationType: "test"));

    private AdminAiSpendReader Reader(string signedInAs = AdminEmail) => new(
        _db,
        new FakeAuthState(SignedIn(signedInAs)),
        Options.Create(new AdminOptions { Emails = [AdminEmail] }));

    private void SeedUsage(string household, DateOnly day, int calls, long input, long output, long cost)
    {
        _db.HouseholdId = household;
        using var db = _db.CreateDbContext();
        db.AiUsages.Add(new AiUsage
        {
            Day = day, Calls = calls, InputTokens = input, OutputTokens = output, CostMicros = cost,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task It_sums_ai_spend_across_every_household()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        SeedUsage("hh-a", today, calls: 3, input: 100, output: 40, cost: 1_500_000);
        SeedUsage("hh-b", today, calls: 2, input: 60, output: 10, cost: 500_000);

        var report = await Reader().GetAsync();

        // Both households are in the sum — the point of the IgnoreQueryFilters. Drop it and the read is
        // scoped to the last-pinned household (hh-b) and these become 2 / 70 / 500_000.
        Assert.Equal(5, report.Today.Calls);
        Assert.Equal(210, report.Today.Tokens);           // (100+40) + (60+10)
        Assert.Equal(2_000_000, report.Today.CostMicros); // $2.00 across both
        Assert.Equal(2, report.ActiveHouseholdsThisMonth);
    }

    [Fact]
    public async Task Anyone_but_the_configured_admin_is_refused()
    {
        SeedUsage("hh-a", DateOnly.FromDateTime(DateTime.Today), 1, 10, 10, 1000);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Reader(signedInAs: "wife@example.com").GetAsync());
    }
}
