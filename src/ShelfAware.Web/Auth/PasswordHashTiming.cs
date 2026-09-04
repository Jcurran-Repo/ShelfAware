using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ShelfAware.Web.Auth;

/// <summary>Equalises the cost of a FAILED sign-in so response timing can't reveal whether an account
/// exists or is confirmed.
///
/// Identity's <c>PasswordSignInAsync</c> runs the PBKDF2 password verify (~tens of ms) only when it reaches
/// the password check — a found, CONFIRMED account — and short-circuits before it for an unknown email
/// (returns <c>Failed</c> after the user lookup) or an unconfirmed account (returns <c>NotAllowed</c> from
/// the pre-sign-in gate), each in well under a millisecond. Left alone, that ~1000× gap is a trivially-timed
/// account-existence oracle. On those short-circuit paths the Login page calls <see cref="Equalize"/> to burn
/// one equivalent verify, so every generic-failure response costs the same regardless of account state.
///
/// The throwaway hash is produced ONCE from a <see cref="PasswordHasher{TUser}"/> built from the SAME
/// <see cref="PasswordHasherOptions"/> the app's real hasher uses, so a burn runs the exact PBKDF2 work a
/// real verify does (same algorithm and iteration count) — no magic constant to drift if those options ever
/// change. It builds its own hasher rather than injecting <see cref="IPasswordHasher{TUser}"/> because that
/// is registered SCOPED and this is a singleton (so the one-time hash cost is paid at startup, not per
/// request); the options accessor is a singleton and carries whatever configuration the app set.</summary>
public sealed class PasswordHashTiming
{
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly AppUser _throwawayUser = new();
    private readonly string _throwawayHash;

    public PasswordHashTiming(IOptions<PasswordHasherOptions> hasherOptions)
    {
        _hasher = new PasswordHasher<AppUser>(hasherOptions);
        _throwawayHash = _hasher.HashPassword(_throwawayUser, "not-a-real-password");
    }

    /// <summary>Run one password verify against the throwaway hash, discarding the result — purely to spend
    /// the time a real verify would. Synchronous and CPU-bound (that is the point). Called only on the fast
    /// failure paths, which are already error responses, so the added latency there costs the caller nothing
    /// they'd notice. <c>VerifyHashedPassword</c> does the full PBKDF2 even for a non-matching password (the
    /// final comparison is constant-time), so the wrong password here still burns the real cost.</summary>
    public void Equalize() => _hasher.VerifyHashedPassword(_throwawayUser, _throwawayHash, "still-not-the-password");
}
