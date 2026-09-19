using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// ⚠️ The Demo valve's configuration rules, which startup reads out loud.
///
/// The warning they produce has to be worth reading, so every objection must be provable from the two
/// numbers alone — a rule that fires on a coherent box is one an operator learns to scroll past, and then
/// the real misconfiguration scrolls past with it. Both halves are pinned: what objects, and what doesn't.
/// </summary>
public class DemoOptionsTests
{
    [Fact]
    public void An_unconfigured_section_objects_to_nothing()
    {
        // The family / self-host posture. Configuring no valve is a coherent choice, not a fault, so a box
        // that never meant to be a demo box hears nothing at all.
        Assert.Empty(new DemoOptions().ConfigurationObjections());
    }

    [Fact]
    public void A_cap_below_its_alert_threshold_objects_to_nothing()
    {
        var options = new DemoOptions { DailyGlobalCallLimit = 500, AlertThreshold = 400 };

        Assert.Empty(options.ConfigurationObjections());
    }

    [Fact]
    public void A_cap_with_no_alert_objects_to_nothing()
    {
        // Deliberately running without a heads-up is fine — the bound is still there.
        Assert.Empty(new DemoOptions { DailyGlobalCallLimit = 500 }.ConfigurationObjections());
    }

    [Fact]
    public void An_alert_with_no_cap_objects()
    {
        // The operator believes they have a valve: they'll be told traffic is arriving by a box that will
        // never stop it.
        var objection = Assert.Single(new DemoOptions { AlertThreshold = 400 }.ConfigurationObjections());

        Assert.Contains("DailyGlobalCallLimit", objection, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500)] // equal — the alert can only fire on the call that is already refused
    [InlineData(600)] // above — it can never fire at all
    public void An_alert_at_or_above_the_cap_objects(int alert)
    {
        var options = new DemoOptions { DailyGlobalCallLimit = 500, AlertThreshold = alert };

        var objection = Assert.Single(options.ConfigurationObjections());
        Assert.Contains("AlertThreshold", objection, StringComparison.Ordinal);
        Assert.Contains(alert.ToString(), objection, StringComparison.Ordinal);
    }

    [Fact]
    public void The_alert_one_below_the_cap_is_the_last_coherent_value()
    {
        // The boundary in the direction that matters: 499 against a cap of 500 is the tightest useful
        // heads-up, so the rule must not object to it.
        Assert.Empty(new DemoOptions { DailyGlobalCallLimit = 500, AlertThreshold = 499 }.ConfigurationObjections());
    }
}
