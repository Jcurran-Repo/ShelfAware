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
}
