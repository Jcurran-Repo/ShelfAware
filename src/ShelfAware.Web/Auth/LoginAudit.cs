using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ShelfAware.Web.Auth;

/// <summary>Records a successful sign-in and reads the per-account login stats — the ONE definition of
/// login counting, over auth.db. Called from the interactive sign-in SITES (Login, Register,
/// ExternalLogin, DevAuth) and nowhere else: a login is a human authenticating, so the
/// <c>RefreshSignInAsync</c> paths (ChangePassword, ChooseHousehold — which re-issue the cookie to pick
/// up new claims) are deliberately NOT counted. That is why this isn't hooked at the cookie's
/// OnSigningIn / SignInManager level, which those refreshes funnel through too and would inflate the
/// count. ⚠️ A new interactive-login path must call <see cref="RecordAsync"/>; a refresh must not.
///
/// Reads are served to the admin page through AdminReportReader (which carries the admin gate), the same
/// way the error log is. Recording is best-effort: the audit is secondary to the login itself, so a
/// write failure is logged and swallowed rather than turning a valid sign-in into a 500.</summary>
public sealed class LoginAudit(IDbContextFactory<AuthDbContext> dbFactory, ILogger<LoginAudit> logger)
{
    /// <summary>Count a sign-in for <paramref name="userId"/> (email denormalized for display). Upsert:
    /// increment the existing row, or insert the first one. Best-effort — never throws for a DB problem
    /// (logged), so it can't break the login it's recording; cancellation still propagates.</summary>
    public async Task RecordAsync(string userId, string email, DateTimeOffset at, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            if (await IncrementAsync(db, userId, email, at, ct) > 0) return;

            db.UserLoginStats.Add(new UserLoginStat
            {
                UserId = userId,
                Email = email,
                LoginCount = 1,
                FirstLoginAt = at,
                LastLoginAt = at,
            });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent sign-in for the SAME account (two devices at once) inserted the row
                // between the update above and here — the PK on UserId makes that a constraint failure,
                // not a second row. Fall back to the increment the first writer's row now accepts.
                await IncrementAsync(db, userId, email, at, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The sign-in already succeeded; a login-audit failure must not surface as a failed login.
            logger.LogWarning(ex, "Couldn't record the login for {UserId}; the sign-in still succeeded.", userId);
        }
    }

    /// <summary>Record a sign-in known only by email (the password login path): the account lookup that
    /// finds the stable user id runs INSIDE the best-effort boundary, so an auth.db hiccup on the LOOKUP
    /// can't break a sign-in that already succeeded — the same promise <see cref="RecordAsync"/> makes for
    /// the write. Callers that already hold the account use <see cref="RecordAsync"/> directly.</summary>
    public async Task RecordByEmailAsync(UserManager<AppUser> users, string email, DateTimeOffset at, CancellationToken ct = default)
    {
        try
        {
            var user = await users.FindByEmailAsync(email);
            if (user is not null)
                await RecordAsync(user.Id, user.Email ?? email, at, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't look up {Email} to record its login; the sign-in still succeeded.", email);
        }
    }

    /// <summary>The external-login twin of <see cref="RecordByEmailAsync"/>: resolve the account behind a
    /// provider login and record it, with the lookup inside the best-effort boundary.</summary>
    public async Task RecordByLoginAsync(UserManager<AppUser> users, string provider, string providerKey, DateTimeOffset at, CancellationToken ct = default)
    {
        try
        {
            var user = await users.FindByLoginAsync(provider, providerKey);
            if (user is not null)
                await RecordAsync(user.Id, user.Email ?? user.UserName ?? user.Id, at, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't look up the {Provider} account to record its login; the sign-in still succeeded.", provider);
        }
    }

    private static Task<int> IncrementAsync(AuthDbContext db, string userId, string email, DateTimeOffset at, CancellationToken ct) =>
        db.UserLoginStats.Where(s => s.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LoginCount, x => x.LoginCount + 1)
                .SetProperty(x => x.LastLoginAt, at)
                .SetProperty(x => x.Email, email), ct);

    /// <summary>Every account's login stats, most-recently-active first. Ordered client-side because
    /// SQLite can't <c>ORDER BY</c> a <c>DateTimeOffset</c> in SQL (the error log's constraint); the row
    /// count is one-per-account, so the fetch-then-sort is free.</summary>
    public async Task<List<UserLoginStat>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.UserLoginStats.AsNoTracking().ToListAsync(ct);
        return [.. rows.OrderByDescending(s => s.LastLoginAt).ThenBy(s => s.Email)];
    }
}
