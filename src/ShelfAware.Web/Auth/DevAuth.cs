using Microsoft.AspNetCore.Identity;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Auth;

/// <summary>
/// A Development-only quick sign-in, so a dev server can be driven past the auth wall without a manual
/// registration on every run. The code ships in every build, but it can only ever ACTIVATE on a
/// developer's machine: <see cref="IsEnabled"/> requires the Development environment AND an explicit
/// opt-in flag, and a Production deployment can never satisfy the first conjunct — so the family box,
/// the droplet, and the tailnet publish (all run Production) never map the endpoint no matter what
/// their config says. That "never in production" guarantee is the whole safety property, and
/// <c>DevAuthTests</c> pins it.
/// </summary>
public static class DevAuth
{
    public const string ConfigKey = "Dev:QuickLogin";
    private const string DevEmail = "dev@shelfaware.local";

    /// <summary>THE gate: Development AND the opt-in flag. Production fails <c>IsDevelopment()</c>
    /// unconditionally, which is what makes this impossible to turn on anywhere real.</summary>
    public static bool IsEnabled(IHostEnvironment env, IConfiguration config)
        => env.IsDevelopment() && config.GetValue<bool>(ConfigKey);

    /// <summary>Maps <c>GET /dev/login</c> when (and only when) <see cref="IsEnabled"/>. It signs in a
    /// passwordless dev account in a sample-seeded "Dev Sandbox" household, then redirects home.
    /// Idempotent — the account and its data are created once and reused on every later hit.</summary>
    public static void MapDevQuickLogin(this WebApplication app)
    {
        // Not mapped at all outside dev. The endpoint therefore doesn't exist to be hit in production;
        // the in-handler re-check below is belt-and-suspenders against a future unconditional map.
        if (!IsEnabled(app.Environment, app.Configuration)) return;

        app.MapGet("/dev/login", async (
            IHostEnvironment env, IConfiguration config,
            UserManager<AppUser> users, SignInManager<AppUser> signIn,
            HouseholdService households, AuthDbContext authDb,
            ICurrentHousehold current, DemoDataSeeder seeder,
            ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("DevAuth");
            if (!IsEnabled(env, config)) return Results.NotFound();

            var user = await users.FindByEmailAsync(DevEmail);
            if (user is null)
            {
                // One transaction across user + household, same as registration, so a failure leaves no
                // orphaned half-account.
                await using var tx = await authDb.Database.BeginTransactionAsync();
                user = new AppUser { UserName = DevEmail, Email = DevEmail, EmailConfirmed = true };
                // Passwordless: this account is only ever signed in through here, never the login form,
                // so there is no dev credential to hardcode in source.
                var created = await users.CreateAsync(user);
                if (!created.Succeeded)
                {
                    return Results.Problem("dev quick-login couldn't create the dev user: "
                        + string.Join("; ", created.Errors.Select(e => e.Description)));
                }
                await households.CreateForAsync("Dev Sandbox", user);
                await tx.CommitAsync();
                log.LogInformation("dev quick-login: created the dev account and its sandbox household.");
            }

            // Sign in AFTER the household exists so the issued cookie carries the household claim (the
            // claims factory reads user.HouseholdId) — the same ordering registration uses.
            await signIn.SignInAsync(user, isPersistent: true);

            // Fill the sandbox with the sample catalog once, so a fresh dev DB isn't a ghost town.
            // Best-effort: being signed in is the point, and the onboarding banner can seed it too, so a
            // seed failure logs and still lands you on the dashboard rather than blocking the login.
            if (user.HouseholdId is { } householdId)
            {
                try
                {
                    current.UseFixed(householdId);
                    await seeder.SeedAsync();
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "dev quick-login: sample seed skipped.");
                }
            }

            return Results.Redirect("/");
        }).AllowAnonymous();
    }
}
