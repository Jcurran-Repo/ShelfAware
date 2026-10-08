using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// Pins down household resolution, especially the detached-background-task case that would otherwise
/// throw: the persistent voice agent runs its turn loop off the component sync context, so the
/// AuthenticationStateProvider isn't callable there — the household must already be pinned.
/// </summary>
public class CurrentHouseholdTests
{
    private const string Claim = HouseholdClaimsPrincipalFactory.HouseholdClaim;

    private static CurrentHousehold Build(Action<ServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        configure?.Invoke(services);
        return new CurrentHousehold(services.BuildServiceProvider());
    }

    private static IHttpContextAccessor AccessorWithHousehold(string? householdId)
    {
        var identity = householdId is null ? new ClaimsIdentity() : new ClaimsIdentity([new Claim(Claim, householdId)], "test");
        return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
    }

    /// <summary>Simulates the out-of-context case: a circuit provider that throws when called off the
    /// component sync context (exactly what real detached background work triggers).</summary>
    private sealed class ThrowingAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            throw new InvalidOperationException("Do not call GetAuthenticationStateAsync outside of the DI scope for a Razor component.");
    }

    private sealed class StaticAuthStateProvider(string householdId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(Claim, householdId)], "test"))));
    }

    [Fact]
    public async Task A_pinned_household_resolves_even_when_the_auth_provider_would_throw()
    {
        // The regression this fix targets: HouseholdInitializer pins in-context, then the voice loop's
        // out-of-context resolution succeeds off the cache instead of hitting the throwing provider.
        var current = Build(s => s.AddSingleton<AuthenticationStateProvider, ThrowingAuthStateProvider>());
        current.UseFixed("hh-pinned");

        Assert.Equal("hh-pinned", await current.GetRequiredIdAsync());
        Assert.Equal("hh-pinned", await current.GetIdAsync());
    }

    [Fact]
    public async Task It_resolves_from_the_HttpContext_claim_for_api_and_static_requests()
    {
        var current = Build(s => s.AddSingleton(AccessorWithHousehold("hh-http")));
        Assert.Equal("hh-http", await current.GetRequiredIdAsync());
    }

    [Fact]
    public async Task It_falls_back_to_the_circuit_auth_state_when_there_is_no_HttpContext()
    {
        var current = Build(s => s.AddSingleton<AuthenticationStateProvider>(new StaticAuthStateProvider("hh-circuit")));
        Assert.Equal("hh-circuit", await current.GetRequiredIdAsync());
    }

    [Fact]
    public async Task An_out_of_context_provider_with_no_pin_fails_safe_rather_than_guessing()
    {
        // No pin, no HttpContext, provider throws → we refuse rather than silently touch some tenant.
        var current = Build(s => s.AddSingleton<AuthenticationStateProvider, ThrowingAuthStateProvider>());

        Assert.Null(await current.GetIdAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await current.GetRequiredIdAsync());
    }

    [Fact]
    public async Task The_resolved_household_is_cached_so_the_provider_is_hit_at_most_once()
    {
        var provider = new CountingAuthStateProvider("hh-once");
        var current = Build(s => s.AddSingleton<AuthenticationStateProvider>(provider));

        await current.GetRequiredIdAsync();
        await current.GetRequiredIdAsync();

        Assert.Equal(1, provider.Calls);
    }

    private sealed class CountingAuthStateProvider(string householdId) : AuthenticationStateProvider
    {
        public int Calls { get; private set; }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            Calls++;
            return Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(Claim, householdId)], "test"))));
        }
    }

    // --- the person -----------------------------------------------------------------------------

    private static ClaimsPrincipal Person(string householdId, string userId, params Claim[] extra) =>
        new(new ClaimsIdentity(
            [new Claim(Claim, householdId), new Claim(ClaimTypes.NameIdentifier, userId), .. extra], "test"));

    private sealed class PrincipalAuthStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    [Fact]
    public async Task The_member_resolves_from_the_same_sign_in_as_the_household()
    {
        var current = Build(s => s.AddSingleton<AuthenticationStateProvider>(
            new PrincipalAuthStateProvider(Person("hh-circuit", "user-42"))));

        Assert.Equal("user-42", await current.GetMemberIdAsync());
        Assert.Equal("hh-circuit", await current.GetIdAsync());
    }

    [Fact]
    public async Task A_member_pin_answers_out_of_context_like_the_household_pin()
    {
        // HouseholdInitializer pins both in-context; the voice agent's detached loop then logs a meal.
        var current = Build(s => s.AddSingleton<AuthenticationStateProvider, ThrowingAuthStateProvider>());
        current.UseFixed("hh-pinned");
        current.UseFixedMember("user-pinned");

        Assert.Equal("user-pinned", await current.GetMemberIdAsync());
    }

    [Fact]
    public async Task A_household_pinned_without_a_member_is_background_work_and_names_nobody()
    {
        // The meal-plan job pins a household; it must not borrow whatever principal is ambient — which here
        // belongs to a different household altogether.
        var current = Build(s => s.AddSingleton<AuthenticationStateProvider>(
            new PrincipalAuthStateProvider(Person("hh-someone-else", "user-ambient"))));
        current.UseFixed("hh-job");

        Assert.Null(await current.GetMemberIdAsync());
    }

    [Fact]
    public async Task An_api_token_speaks_for_its_household_not_for_whoever_minted_it()
    {
        var token = Person("hh-api", "user-minter", new Claim(ApiTokenAuthenticationHandler.TokenIdClaim, "7"));
        var current = Build(s => s.AddSingleton<IHttpContextAccessor>(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = token } }));

        Assert.Equal("hh-api", await current.GetIdAsync());
        Assert.Null(await current.GetMemberIdAsync());
    }

    [Fact]
    public async Task No_sign_in_names_no_member()
    {
        Assert.Null(await Build().GetMemberIdAsync());
        var outOfContext = Build(s => s.AddSingleton<AuthenticationStateProvider, ThrowingAuthStateProvider>());
        Assert.Null(await outOfContext.GetMemberIdAsync());
    }

    [Fact]
    public async Task An_unresolved_lookup_is_not_cached_so_a_later_in_context_call_still_finds_the_sign_in()
    {
        var provider = new FlippingAuthStateProvider(Person("hh-late", "user-late"));
        var current = Build(s => s.AddSingleton<AuthenticationStateProvider>(provider));

        Assert.Null(await current.GetIdAsync());      // first call: out of context
        provider.InContext = true;
        Assert.Equal("hh-late", await current.GetIdAsync());
        Assert.Equal("user-late", await current.GetMemberIdAsync());
    }

    private sealed class FlippingAuthStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public bool InContext { get; set; }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => InContext
            ? Task.FromResult(new AuthenticationState(principal))
            : throw new InvalidOperationException("outside a Razor component's DI scope");
    }
}
