using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The Auth: options' defaults and the one startup rule Program.cs builds from them —
/// <see cref="AuthOptions.EmailConfirmationSatisfiable"/>. The rule is load-bearing: get it wrong and the
/// demo box either boots into a state where nobody can confirm (and so nobody can sign in), or refuses to
/// boot when it's actually fine.
/// </summary>
public class AuthOptionsTests
{
    [Fact]
    public void The_new_demo_controls_default_off()
    {
        // Self-host / family posture: no email-confirmation requirement, no account cap. Turning either on
        // is a deliberate per-box opt-in.
        var o = new AuthOptions();
        Assert.False(o.RequireEmailConfirmation);
        Assert.Null(o.DailyAccountCreationLimit);
    }

    [Theory]
    // (requireConfirmation, emailConfigured) → satisfiable?
    [InlineData(false, false, true)] // not required → fine with or without a mailer
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]   // required AND a mailer present → fine
    [InlineData(true, false, false)] // required but NO mailer → the boot-breaking combination the rule refuses
    public void Email_confirmation_needs_a_mailer_only_when_it_is_required(
        bool requireConfirmation, bool emailConfigured, bool expected)
    {
        Assert.Equal(expected, AuthOptions.EmailConfirmationSatisfiable(requireConfirmation, emailConfigured));
    }
}
