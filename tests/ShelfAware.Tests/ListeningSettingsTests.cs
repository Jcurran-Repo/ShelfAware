using ShelfAware.Core.Speech;

namespace ShelfAware.Tests;

/// <summary>
/// The calibration policy. This is the half of adaptive listening that can be tested without a
/// microphone — the browser measures, this decides — so it's where the reasoning gets pinned.
/// </summary>
public class ListeningSettingsTests
{
    // A plausible quiet-kitchen run: a low hum, a clear voice, someone who pauses briefly mid-question.
    private static CalibrationSample Typical => new(
        NoiseFloor: 0.01, SpeechPeak: 0.25, LongestPauseMs: 400, LongestUtteranceMs: 2200);

    [Fact]
    public void A_good_run_produces_settings()
    {
        Assert.True(ListeningSettings.TryFromCalibration(Typical, out var s));
        Assert.NotEqual(ListeningSettings.Default, s);
    }

    // The gate belongs BETWEEN the room and the voice. An arithmetic midpoint would sit near the voice
    // and clip quiet words; the geometric one respects that loudness is a ratio scale.
    [Fact]
    public void The_noise_gate_lands_between_the_room_and_the_voice()
    {
        ListeningSettings.TryFromCalibration(Typical, out var s);
        var threshold = Typical.NoiseFloor * s.FloorMultiple;

        Assert.True(threshold > Typical.NoiseFloor, "gate must ignore the room");
        Assert.True(threshold < Typical.SpeechPeak, "gate must not ignore the voice");
        // sqrt(0.01 * 0.25) = 0.05
        Assert.Equal(0.05, threshold, precision: 3);
    }

    // The number nobody can guess for someone else: silence has to outlast how long THEY pause to think.
    [Fact]
    public void Silence_outlasts_their_longest_mid_sentence_pause()
    {
        ListeningSettings.TryFromCalibration(Typical, out var s);
        Assert.True(s.SilenceMs > Typical.LongestPauseMs);
    }

    [Fact]
    public void A_thoughtful_speaker_gets_a_longer_silence_window_than_a_brisk_one()
    {
        ListeningSettings.TryFromCalibration(Typical with { LongestPauseMs = 200 }, out var brisk);
        ListeningSettings.TryFromCalibration(Typical with { LongestPauseMs = 1200 }, out var thoughtful);
        Assert.True(thoughtful.SilenceMs > brisk.SilenceMs);
    }

    // A loud room needs a HIGHER absolute gate but a LOWER multiple — the floor it multiplies is bigger.
    [Fact]
    public void A_noisy_room_gates_higher_in_absolute_terms()
    {
        ListeningSettings.TryFromCalibration(new(0.002, 0.25, 400, 2200), out var quiet);
        ListeningSettings.TryFromCalibration(new(0.05, 0.25, 400, 2200), out var noisy);

        Assert.True(0.05 * noisy.FloorMultiple > 0.002 * quiet.FloorMultiple);
        Assert.True(noisy.FloorMultiple < quiet.FloorMultiple);
    }

    // The failure that matters: they never spoke. Concluding anything from that gives a gate that never
    // opens or never shuts, and the user would have no idea why — so say so instead.
    [Theory]
    [InlineData(0.01, 0.011, 400, 2200)] // voice barely above the room — the same number in a hat
    [InlineData(0.01, 0.0, 400, 2200)]   // silence
    [InlineData(0.01, 0.25, 400, 50)]    // a blip, not speech
    public void A_run_that_never_heard_a_person_fails_and_keeps_the_defaults(
        double floor, double peak, int pause, int utterance)
    {
        var ok = ListeningSettings.TryFromCalibration(new(floor, peak, pause, utterance), out var s);

        Assert.False(ok);
        Assert.Equal(ListeningSettings.Default, s);
    }

    [Fact]
    public void Calibration_output_is_always_within_safe_bounds()
    {
        // A slammed cupboard during calibration, and someone who paused for an age.
        ListeningSettings.TryFromCalibration(new(0.0001, 0.99, 9000, 90000), out var s);

        Assert.InRange(s.SilenceMs, 400, 3000);
        Assert.InRange(s.MaxMs, 5000, 60000);
        Assert.InRange(s.FloorMultiple, 1.5, 15.0);
        Assert.InRange(s.MinThreshold, 0.002, 0.2);
    }

    // A hand-edited or corrupted stored value must not be able to make the reader deaf or endless.
    [Fact]
    public void Nonsense_settings_clamp_into_range()
    {
        var s = new ListeningSettings(-5000, 0, 1, -3, 99).Clamped();

        Assert.InRange(s.SilenceMs, 400, 3000);
        Assert.InRange(s.OpenMs, 2000, 20000);
        Assert.InRange(s.FloorMultiple, 1.5, 15.0);
        Assert.InRange(s.MinThreshold, 0.002, 0.2);
    }

    [Fact]
    public void A_not_a_number_multiple_clamps_rather_than_poisoning_the_gate() =>
        Assert.Equal(1.5, (ListeningSettings.Default with { FloorMultiple = double.NaN }).Clamped().FloorMultiple);

    // A cap shorter than the silence window would end every utterance before it could end itself —
    // the reader would look like it was cutting people off for no reason.
    [Fact]
    public void The_utterance_cap_can_never_undercut_the_silence_window()
    {
        var s = new ListeningSettings(SilenceMs: 3000, OpenMs: 6000, MaxMs: 1000,
            FloorMultiple: 3, MinThreshold: 0.01).Clamped();

        Assert.True(s.MaxMs > s.SilenceMs);
    }

    [Fact]
    public void The_defaults_are_self_consistent() =>
        Assert.Equal(ListeningSettings.Default, ListeningSettings.Default.Clamped());

    [Fact]
    public void The_utterance_cap_is_the_longest_utterance_times_the_headroom()
    {
        // 5000 ms × 1.6 = 8000, comfortably inside the clamp (a brisk pause keeps the floor at 5000).
        ListeningSettings.TryFromCalibration(Typical with { LongestUtteranceMs = 5000 }, out var s);
        Assert.Equal(8000, s.MaxMs);
    }

    [Fact]
    public void The_minimum_threshold_is_a_fraction_of_the_speech_peak()
    {
        // 0.25 × 0.06 = 0.015, inside the [0.002, 0.2] clamp.
        ListeningSettings.TryFromCalibration(Typical, out var s);
        Assert.Equal(0.015, s.MinThreshold, precision: 5);
    }

    [Fact]
    public void The_utterance_cap_floor_is_three_times_the_silence_window()
    {
        // A long thinker (pause 2000 -> silence 2350) with a short utterance: the cap can't drop below
        // 3 × the silence window (7050), not the flat 5000 floor.
        ListeningSettings.TryFromCalibration(
            Typical with { LongestPauseMs = 2000, LongestUtteranceMs = 300 }, out var s);
        Assert.Equal(7050, s.MaxMs);
    }

    [Fact]
    public void An_utterance_of_exactly_the_minimum_length_is_speech()
    {
        // The blip floor is inclusive at 200 ms.
        Assert.True(ListeningSettings.TryFromCalibration(Typical with { LongestUtteranceMs = 200 }, out _));
    }

    [Fact]
    public void Speech_exactly_at_the_ratio_floor_is_not_enough()
    {
        // The gate needs speech to EXCEED 1.5× the room, not merely meet it: 0.75 == 0.5 × 1.5 fails.
        Assert.False(ListeningSettings.TryFromCalibration(new(0.5, 0.75, 400, 2200), out _));
    }
}
