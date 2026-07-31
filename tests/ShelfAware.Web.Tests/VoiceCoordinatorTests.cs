using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>The per-circuit bus between the layout-hosted voice agent and the pages. The one subtle
/// contract: EVERY subscriber is awaited — a plain multicast Invoke() awaits only the LAST delegate,
/// so an earlier subscriber's reload could still be mid-flight when the notifier moved on.</summary>
public class VoiceCoordinatorTests
{
    [Fact]
    public async Task Every_subscriber_is_awaited_not_just_the_last()
    {
        var coordinator = new VoiceCoordinator();
        var completed = new List<string>();
        coordinator.PantryChanged += async () => { await Task.Yield(); completed.Add("first"); };
        coordinator.PantryChanged += async () => { await Task.Yield(); completed.Add("second"); };

        await coordinator.NotifyPantryChangedAsync();

        Assert.Equal(["first", "second"], completed); // both ran to completion, in subscription order
    }

    [Fact]
    public async Task Notifying_with_no_subscribers_is_a_no_op()
    {
        var coordinator = new VoiceCoordinator();

        await coordinator.NotifyPantryChangedAsync();
        await coordinator.RequestResumeAsync();
        await coordinator.RequestStandDownAsync();
    }

    [Fact]
    public async Task The_three_signals_are_distinct_channels()
    {
        // A reader taking the mic (StandDown) must not look like data changing (PantryChanged) — a
        // crossed wire here would reload pages on every hand-off.
        var coordinator = new VoiceCoordinator();
        var raised = new List<string>();
        coordinator.PantryChanged += () => { raised.Add("pantry"); return Task.CompletedTask; };
        coordinator.ResumeRequested += () => { raised.Add("resume"); return Task.CompletedTask; };
        coordinator.StandDownRequested += () => { raised.Add("standdown"); return Task.CompletedTask; };

        await coordinator.RequestStandDownAsync();
        await coordinator.RequestResumeAsync();

        Assert.Equal(["standdown", "resume"], raised);
    }
}
