using Microsoft.AspNetCore.Components.Authorization;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Data;

/// <summary>Resolves which household the current scope acts for. See <see cref="CurrentHousehold"/>.</summary>
public interface ICurrentHousehold
{
    /// <summary>The current household id, or null when the scope has no signed-in user.</summary>
    ValueTask<string?> GetIdAsync(CancellationToken cancellationToken = default);

    /// <summary>The current household id, throwing when unresolvable — data access must never
    /// silently fall through to someone else's (or nobody's) pantry.</summary>
    ValueTask<string> GetRequiredIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Pins this scope to an explicit household — for background work (the startup receipt
    /// scan) that acts on behalf of a household with no user attached.</summary>
    void UseFixed(string householdId);
}

/// <summary>Scoped. Resolution order: an explicit <see cref="UseFixed"/> pin → the <c>HttpContext</c>
/// principal's household claim (minimal APIs, static SSR) → the circuit's
/// <see cref="AuthenticationStateProvider"/> (interactive pages). The claim is baked into the cookie at
/// sign-in, so no DB lookup happens here. Cached per scope — a user's household never changes mid-session
/// by design (switching is future work).
///
/// The <see cref="AuthenticationStateProvider"/> step only works INSIDE a component's synchronization
/// context; a detached background task (e.g. the persistent voice agent's turn loop) calling it throws.
/// So in a circuit the household is pinned up-front via <see cref="UseFixed"/> by
/// <c>HouseholdInitializer</c> (in the layout, in-context), and every later out-of-context resolution
/// then uses the cached pin. If it were somehow never pinned, <see cref="GetRequiredIdAsync"/> throwing
/// is the correct failsafe — better to fail than guess a tenant.</summary>
public sealed class CurrentHousehold(IServiceProvider services) : ICurrentHousehold
{
    private string? _id;

    public void UseFixed(string householdId) => _id = householdId;

    public async ValueTask<string?> GetIdAsync(CancellationToken cancellationToken = default)
    {
        if (_id is not null) return _id;

        var claim = services.GetService<IHttpContextAccessor>()?.HttpContext?
            .User.FindFirst(HouseholdClaimsPrincipalFactory.HouseholdClaim)?.Value;

        if (claim is null &&
            services.GetService<AuthenticationStateProvider>() is { } authState)
        {
            try
            {
                var state = await authState.GetAuthenticationStateAsync();
                claim = state.User.FindFirst(HouseholdClaimsPrincipalFactory.HouseholdClaim)?.Value;
            }
            catch (InvalidOperationException)
            {
                // Not a circuit scope: the provider resolves anywhere (it's scoped) but only ANSWERS
                // inside a Razor-component scope, throwing otherwise. That just means "no user here" —
                // fall through unresolved so callers get the pointed no-household message, not this one.
            }
        }

        return _id = claim;
    }

    public async ValueTask<string> GetRequiredIdAsync(CancellationToken cancellationToken = default)
        => await GetIdAsync(cancellationToken) ?? throw new InvalidOperationException(
            "No current household: the scope has no signed-in user and no UseFixed() pin. " +
            "Interactive pages and APIs get one from the auth cookie; background work must call " +
            $"{nameof(ICurrentHousehold)}.{nameof(UseFixed)} before touching pantry data.");
}
