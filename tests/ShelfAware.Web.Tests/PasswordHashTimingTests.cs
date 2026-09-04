using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// <see cref="PasswordHashTiming.Equalize"/> exists to burn one real password verify on the fast
/// failed-sign-in paths, so login-response timing can't reveal whether an account exists or is confirmed.
/// If a burn stopped doing real PBKDF2 work (a malformed throwaway hash that fast-fails, say), the timing
/// oracle would quietly reopen with a green suite — so pin that it genuinely costs hashing time.
/// </summary>
public class PasswordHashTimingTests
{
    [Fact]
    public void Equalize_does_not_throw()
    {
        var timing = new PasswordHashTiming(Options.Create(new PasswordHasherOptions()));

        var ex = Record.Exception(timing.Equalize);

        Assert.Null(ex);
    }

    [Fact]
    public void Equalize_actually_runs_a_password_verify_so_it_costs_hashing_time()
    {
        var timing = new PasswordHashTiming(Options.Create(new PasswordHasherOptions()));

        // A real PBKDF2 verify is milliseconds each; a no-op burn (skipped, or a malformed hash that
        // fast-fails) would be microseconds. Ten burns must take well past 10ms or the equalizer isn't doing
        // the work it exists for. The margin is ~10-40x above the threshold and ~100x below a no-op, so a
        // fast CI box doesn't flake.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            timing.Equalize();
        }
        sw.Stop();

        Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(10),
            $"Ten equalizing verifies took only {sw.ElapsedMilliseconds}ms — the burn isn't doing real PBKDF2 work.");
    }
}
