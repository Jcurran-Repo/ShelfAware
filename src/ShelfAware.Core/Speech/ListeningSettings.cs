namespace ShelfAware.Core.Speech;

/// <summary>
/// What a listening window should do in THIS room, with THIS microphone, for THIS person. Every value
/// began as a guess, which is the whole reason for <see cref="TryFromCalibration"/> — a kitchen with an
/// extractor fan and a cook who pauses mid-sentence has no business sharing constants with a quiet
/// office and someone who doesn't.
///
/// Stored per browser rather than per household: two people in the same kitchen still have different
/// microphones, and one of them is on a phone.
/// </summary>
/// <param name="SilenceMs">How much quiet means they've finished talking. The most consequential value:
/// too short guillotines them mid-sentence, too long makes every command feel laggy.</param>
/// <param name="OpenMs">How long to hold the window open when nobody says anything at all.</param>
/// <param name="MaxMs">Hard cap on a single utterance, so a loud room can't hold the mic forever.</param>
/// <param name="FloorMultiple">Speech is louder than the room by this factor. The noise gate.</param>
/// <param name="MinThreshold">Absolute floor under the gate, so a silent room doesn't make it infinitely
/// twitchy (multiplying near-zero by anything is still near-zero).</param>
public sealed record ListeningSettings(
    int SilenceMs,
    int OpenMs,
    int MaxMs,
    double FloorMultiple,
    double MinThreshold)
{
    /// <summary>The pre-calibration guesses. Reasonable, and very unlikely to be right for anyone.</summary>
    public static ListeningSettings Default { get; } = new(
        SilenceMs: 900, OpenMs: 6000, MaxMs: 15000, FloorMultiple: 3.0, MinThreshold: 0.012);

    // A person who pauses to think mid-sentence should still be allowed to finish it, so silence has to
    // outlast their longest INTERNAL pause by a margin rather than merely match it.
    private const int PauseMarginMs = 350;

    // Someone's longest calibration utterance is a sample of one; leave room for a wordier question.
    private const double UtteranceHeadroom = 1.6;

    // Speech must clear the room by this much for a calibration to mean anything. Below it, the two
    // measurements are the same number wearing a hat.
    private const double MinimumSpeechToNoiseRatio = 1.5;

    /// <summary>
    /// Turn one calibration run into settings, or fail honestly. Returns false — leaving
    /// <paramref name="settings"/> at <see cref="Default"/> — when the sample can't support a conclusion,
    /// which in practice means the microphone never heard the person speak. Guessing from that would
    /// produce a gate that either never opens or never closes, and the user would have no idea why.
    /// </summary>
    public static bool TryFromCalibration(CalibrationSample sample, out ListeningSettings settings)
    {
        settings = Default;
        if (!sample.IsUsable) return false;

        // Put the gate between the room and the voice, geometrically. Loudness lives on a ratio scale —
        // the midpoint between a hum and a sentence is their product's square root, not their average,
        // which would sit far too close to the voice and clip quiet words.
        var floor = Math.Max(sample.NoiseFloor, 1e-6);
        var threshold = Math.Sqrt(floor * sample.SpeechPeak);

        settings = new ListeningSettings(
            SilenceMs: sample.LongestPauseMs + PauseMarginMs,
            OpenMs: Default.OpenMs, // nothing in a calibration says how long to wait for someone silent
            MaxMs: (int)(sample.LongestUtteranceMs * UtteranceHeadroom),
            FloorMultiple: threshold / floor,
            // Derived from their voice, not from the room: in a silent room the multiple is meaningless
            // and this is the only thing keeping the gate from triggering on nothing.
            MinThreshold: sample.SpeechPeak * 0.06).Clamped();

        return true;
    }

    /// <summary>
    /// Force every value into a range that still behaves. Calibration can be handed a slammed cupboard
    /// or a whisper, and a saved setting can be hand-edited to nonsense; neither should be able to make
    /// the reader deaf or leave it listening forever.
    /// </summary>
    public ListeningSettings Clamped() => new(
        SilenceMs: Clamp(SilenceMs, 400, 3000),
        OpenMs: Clamp(OpenMs, 2000, 20000),
        // A cap below the silence window would end every utterance before it could end itself.
        MaxMs: Clamp(MaxMs, Math.Max(5000, Clamp(SilenceMs, 400, 3000) * 3), 60000),
        FloorMultiple: Clamp(FloorMultiple, 1.5, 15.0),
        MinThreshold: Clamp(MinThreshold, 0.002, 0.2));

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
    private static double Clamp(double v, double lo, double hi) =>
        double.IsNaN(v) ? lo : v < lo ? lo : v > hi ? hi : v;
}

/// <summary>
/// One calibration run's raw measurements, taken in the browser because it's the only thing that can
/// hear the room. Deliberately just numbers: the policy that turns them into settings lives in
/// <see cref="ListeningSettings.TryFromCalibration"/>, where it can be tested without a microphone.
/// </summary>
/// <param name="NoiseFloor">RMS of the room with nobody talking.</param>
/// <param name="SpeechPeak">Loudest RMS while they were talking.</param>
/// <param name="LongestPauseMs">Longest gap between words WITHIN one utterance — the number that decides
/// whether we cut people off, and the one nobody could guess for someone else.</param>
/// <param name="LongestUtteranceMs">Longest single thing they said, start to finish.</param>
public sealed record CalibrationSample(
    double NoiseFloor,
    double SpeechPeak,
    int LongestPauseMs,
    int LongestUtteranceMs)
{
    /// <summary>Whether this run heard an actual person. False when the mic caught nothing louder than
    /// the room, or caught only a blip too short to be speech.</summary>
    public bool IsUsable =>
        SpeechPeak > 0
        && LongestUtteranceMs >= 200
        && SpeechPeak > Math.Max(NoiseFloor, 1e-6) * 1.5;
}
