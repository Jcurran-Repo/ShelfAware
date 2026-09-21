using ShelfAware.Core.Speech;

namespace ShelfAware.Tests;

/// <summary>
/// <see cref="VoiceEar"/> is the one definition of "can this box hear, and if not, whose problem is
/// that" — asked by every microphone affordance and by the transcriber's own failure copy.
///
/// <para>The discriminating case is the MANAGED, keyless box (the demo box from 2026-09-21): before
/// this existed, every keyless miss answered "Add your ElevenLabs key in Settings", which on a managed
/// deployment names a panel that is hidden and a key that <c>CircuitVoiceCredentials.Apply</c>
/// deliberately ignores. A single `IsNullOrWhiteSpace(ApiKey)` check cannot tell those two apart, so
/// these tests fail the moment anyone collapses the state back into a bool.</para>
/// </summary>
public class VoiceEarTests
{
    [Fact]
    public void A_key_means_the_box_can_hear_whoever_supplied_it()
    {
        Assert.Equal(EarAvailability.Ready, VoiceEar.Availability(managed: false, "visitor-key"));
        Assert.Equal(EarAvailability.Ready, VoiceEar.Availability(managed: true, "host-key"));
    }

    [Fact]
    public void A_byok_box_with_no_key_points_the_visitor_at_Settings()
    {
        var ear = VoiceEar.Availability(managed: false, apiKey: "");

        Assert.Equal(EarAvailability.NeedsVisitorKey, ear);
        Assert.False(ear.CanHear());
        // The instruction is only honest where the panel exists — that is the whole distinction.
        Assert.Equal("Add your ElevenLabs key in Settings to use voice.", ear.WhyNot());
    }

    [Fact]
    public void A_managed_box_with_no_host_key_says_so_instead_of_asking_for_one()
    {
        var ear = VoiceEar.Availability(managed: true, apiKey: "");

        Assert.Equal(EarAvailability.NotOnThisBox, ear);
        Assert.False(ear.CanHear());
        Assert.Equal("Voice input isn't available on this box.", ear.WhyNot());
        // ⚠️ The regression this file exists for: never send a managed visitor to Settings.
        Assert.DoesNotContain("Settings", ear.WhyNot());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Whitespace_is_not_a_key(string? key)
    {
        Assert.False(VoiceEar.Availability(managed: false, key).CanHear());
        Assert.False(VoiceEar.Availability(managed: true, key).CanHear());
    }

    [Fact]
    public void A_box_that_can_hear_has_nothing_to_explain()
    {
        Assert.Null(VoiceEar.Availability(managed: true, "host-key").WhyNot());
        Assert.True(VoiceEar.Availability(managed: true, "host-key").CanHear());
    }
}
