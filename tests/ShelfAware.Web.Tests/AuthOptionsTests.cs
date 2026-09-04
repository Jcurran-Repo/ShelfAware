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
        // is a deliberate per-box opt-in. With confirmation off and nothing configured, the EFFECTIVE cap is
        // also null (uncapped) — the default only applies on a confirmation-required box.
        var o = new AuthOptions();
        Assert.False(o.RequireEmailConfirmation);
        Assert.Null(o.DailyAccountCreationLimit);
        Assert.Null(o.EffectiveDailyAccountCreationLimit);
    }

    [Theory]
    // (explicit DailyAccountCreationLimit, RequireEmailConfirmation) → effective cap
    [InlineData(null, false, null)]                                        // direct box, none set → uncapped
    [InlineData(null, true, AuthOptions.DefaultDailyAccountCreationLimit)] // confirm box, none set → the default
    [InlineData(25, false, 25)]                                           // explicit always wins...
    [InlineData(25, true, 25)]                                            // ...on either box
    public void The_effective_cap_defaults_on_a_confirmation_required_box_and_an_explicit_value_overrides(
        int? explicitLimit, bool requireConfirmation, int? expected)
    {
        var o = new AuthOptions
        {
            DailyAccountCreationLimit = explicitLimit,
            RequireEmailConfirmation = requireConfirmation,
        };
        Assert.Equal(expected, o.EffectiveDailyAccountCreationLimit);
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
