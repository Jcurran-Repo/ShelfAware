using System.Security.Claims;
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

/// <summary>Which PERSON the current scope acts for, inside its household — the signed-in account's Identity
/// user id. Only per-person data (the meal journal, <c>IMemberOwned</c>) asks; everything else in the pantry
/// is the household's. Resolved by the same object, from the same principal, as <see cref="ICurrentHousehold"/>,
/// so the household and the person can never come from two different sign-ins.</summary>
public interface ICurrentMember
{
    /// <summary>The signed-in person's user id, or null when this scope has no PERSON — no sign-in, a
    /// background job, or an API token (which speaks for a household, not for whoever minted it).</summary>
    ValueTask<string?> GetMemberIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Pins the person, for the same reason <see cref="ICurrentHousehold.UseFixed"/> pins the
    /// household: a detached task (the voice agent's turn loop) cannot ask the auth state itself.</summary>
    void UseFixedMember(string memberId);
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
public sealed class CurrentHousehold(IServiceProvider services) : ICurrentHousehold, ICurrentMember
{
    private string? _id;
    private bool _householdPinned;
    private string? _memberId;
    private bool _memberPinned;
    private ClaimsPrincipal? _principal;

    public void UseFixed(string householdId)
    {
        _id = householdId;
        _householdPinned = true;
    }

    public void UseFixedMember(string memberId)
    {
        _memberId = memberId;
        _memberPinned = true;
    }

    public async ValueTask<string?> GetIdAsync(CancellationToken cancellationToken = default)
    {
        if (_id is not null) return _id;
        return _id = (await PrincipalAsync())?.FindFirst(HouseholdClaimsPrincipalFactory.HouseholdClaim)?.Value;
    }

    public async ValueTask<string> GetRequiredIdAsync(CancellationToken cancellationToken = default)
        => await GetIdAsync(cancellationToken) ?? throw new InvalidOperationException(
            "No current household: the scope has no signed-in user and no UseFixed() pin. " +
            "Interactive pages and APIs get one from the auth cookie; background work must call " +
            $"{nameof(ICurrentHousehold)}.{nameof(UseFixed)} before touching pantry data.");

    public async ValueTask<string?> GetMemberIdAsync(CancellationToken cancellationToken = default)
    {
        if (_memberPinned) return _memberId;
        // ⚠️ A household pinned WITHOUT a member is background work acting for a household (the meal-plan
        // job, dev seeding) — it has no person, and must not borrow one from whatever principal is ambient,
        // which could be a different sign-in than the household it was pinned to.
        if (_householdPinned) return null;
        if (_memberId is not null) return _memberId;
        var principal = await PrincipalAsync();
        // An API token speaks for its household; NameIdentifier on it records who minted it, not who is asking.
        if (principal is null || principal.HasClaim(c => c.Type == ApiTokenAuthenticationHandler.TokenIdClaim))
            return null;
        return _memberId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }

    /// <summary>The signed-in principal — the <c>HttpContext</c>'s (minimal APIs, static SSR), else the
    /// circuit's auth state (interactive pages). Kept once found, so the household and the person always
    /// come from the same sign-in; not kept when nothing answered, so a later in-context call can still
    /// find one (the behaviour the household lookup always had).</summary>
    private async ValueTask<ClaimsPrincipal?> PrincipalAsync()
    {
        if (_principal is not null) return _principal;

        var principal = services.GetService<IHttpContextAccessor>()?.HttpContext?.User;
        if (principal?.FindFirst(HouseholdClaimsPrincipalFactory.HouseholdClaim) is null &&
            services.GetService<AuthenticationStateProvider>() is { } authState)
        {
            try
            {
                principal = (await authState.GetAuthenticationStateAsync()).User;
            }
            catch (InvalidOperationException)
            {
                // Not a circuit scope: the provider resolves anywhere (it's scoped) but only ANSWERS
                // inside a Razor-component scope, throwing otherwise. That just means "no user here" —
                // fall through unresolved so callers get the pointed no-household message, not this one.
                return null;
            }
        }
        // Only a principal that actually carries a household is worth keeping: an anonymous one found
        // mid-sign-in is not the account the rest of this scope will act for.
        return principal?.FindFirst(HouseholdClaimsPrincipalFactory.HouseholdClaim) is null
            ? null
            : _principal = principal;
    }
}
