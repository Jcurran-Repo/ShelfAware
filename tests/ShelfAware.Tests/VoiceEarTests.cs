using ShelfAware.Core.Speech;

namespace ShelfAware.Tests;

/// <summary>
/// <see cref="VoiceEar"/> is the one definition of "can this box hear, and if not, whose problem is
/// that" — asked by every microphone affordance and by the transcriber's own failure copy.
///
/// <para>Two discriminating cases live here, and a single `IsNullOrWhiteSpace(ApiKey)` check gets both
/// wrong. The MANAGED, KEYLESS box: before this existed, every keyless miss answered "Add your
/// ElevenLabs key in Settings", which on a managed deployment names a panel that is hidden and a key
/// that <c>CircuitVoiceCredentials.Apply</c> deliberately ignores. And the LOCAL EAR
/// (<c>Speech:Ear=Moonshine</c>): a box transcribing with a model in its own process hears everyone
/// with no credential anywhere, so asking about a key first would hide the microphone on precisely the
/// box the local ear was built for.</para>
/// </summary>
public class VoiceEarTests
{
    [Fact]
    public void A_key_means_the_box_can_hear_whoever_supplied_it()
    {
        Assert.Equal(EarAvailability.Ready, VoiceEar.Availability(localEar: false, managed: false, "visitor-key"));
        Assert.Equal(EarAvailability.Ready, VoiceEar.Availability(localEar: false, managed: true, "host-key"));
    }

    [Fact]
    public void A_byok_box_with_no_key_points_the_visitor_at_Settings()
    {
        var ear = VoiceEar.Availability(localEar: false, managed: false, apiKey: "");

        Assert.Equal(EarAvailability.NeedsVisitorKey, ear);
        Assert.False(ear.CanHear());
        // The instruction is only honest where the panel exists — that is the whole distinction.
        Assert.Equal("Add your ElevenLabs key in Settings to use voice.", ear.WhyNot());
    }

    [Fact]
    public void A_managed_box_with_no_host_key_says_so_instead_of_asking_for_one()
    {
        var ear = VoiceEar.Availability(localEar: false, managed: true, apiKey: "");

        Assert.Equal(EarAvailability.NotOnThisBox, ear);
        Assert.False(ear.CanHear());
        Assert.Equal("Voice input isn't available on this box.", ear.WhyNot());
        // ⚠️ The regression this file exists for: never send a managed visitor to Settings.
        Assert.DoesNotContain("Settings", ear.WhyNot());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_local_ear_hears_with_no_credential_from_anyone(bool managed)
    {
        // ⚠️ The demo box: managed, no ElevenLabs key, and Speech:Ear=Moonshine. A model in this process
        // is nobody's key, so both the managed and the BYOK shapes hear — and reading the key first
        // would silence exactly the deployment the local ear exists for.
        var ear = VoiceEar.Availability(localEar: true, managed, apiKey: "");

        Assert.Equal(EarAvailability.Ready, ear);
        Assert.True(ear.CanHear());
        Assert.Null(ear.WhyNot());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Whitespace_is_not_a_key(string? key)
    {
        Assert.False(VoiceEar.Availability(localEar: false, managed: false, key).CanHear());
        Assert.False(VoiceEar.Availability(localEar: false, managed: true, key).CanHear());
        // ...but a local ear still hears, because it never wanted one.
        Assert.True(VoiceEar.Availability(localEar: true, managed: true, key).CanHear());
    }

    [Fact]
    public void A_box_that_can_hear_has_nothing_to_explain()
    {
        Assert.Null(VoiceEar.Availability(localEar: false, managed: true, "host-key").WhyNot());
        Assert.True(VoiceEar.Availability(localEar: false, managed: true, "host-key").CanHear());
    }

    [Fact]
    public void An_implementation_that_answers_only_the_key_is_read_as_the_byok_box_it_is()
    {
        // ⚠️ Managed and LocalEar are DEFAULT interface members, so an implementation written before
        // either existed — a stub, an older provider, a test fake — silently takes both defaults. This
        // pins what those defaults mean: the self-host/BYOK shape, where a key is the only way to hear.
        // If someone ever flips a default to true, the boxes that never opted in start offering a
        // microphone they cannot use, and nothing else in the suite would notice.
        IVoiceCredentials keyless = new OnlyAKey("");
        IVoiceCredentials keyed = new OnlyAKey("visitor-key");

        Assert.False(keyless.Managed);
        Assert.False(keyless.LocalEar);
        Assert.Equal(EarAvailability.NeedsVisitorKey, keyless.Ear);
        Assert.Equal("Add your ElevenLabs key in Settings to use voice.", keyless.Ear.WhyNot());
        Assert.Equal(EarAvailability.Ready, keyed.Ear);
    }

    /// <summary>The smallest thing that can implement the interface: a key and nothing else.</summary>
    private sealed record OnlyAKey(string ApiKey) : IVoiceCredentials
    {
        public string AgentId => "";
    }
}
