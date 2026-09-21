namespace ShelfAware.Core.Speech;

/// <summary>Whether this deployment can hear at all, and when it can't, whose problem that is.</summary>
public enum EarAvailability
{
    /// <summary>There is a credential to transcribe with.</summary>
    Ready,

    /// <summary>BYOK, and this visitor hasn't pasted a voice key yet — the Settings panel is right there,
    /// so the ear is one paste away and saying so is useful.</summary>
    NeedsVisitorKey,

    /// <summary>A managed box whose host configured no voice key. Nothing the visitor can do: the key
    /// panel is hidden on a managed deployment and a browser-supplied key is deliberately a no-op
    /// (<c>CircuitVoiceCredentials.Apply</c>), so the ear is simply not a feature of this box.</summary>
    NotOnThisBox,
}

/// <summary>
/// THE definition of "can this box hear", and of what to say when it can't.
///
/// <para>⚠️ It exists because the answer was previously nobody's: three components offered a microphone
/// on a browser-support check alone, and the only credential check lived at the bottom of
/// <c>ElevenLabsSpeechToText</c>, which answered every miss with "Add your ElevenLabs key in Settings".
/// On a MANAGED box that sentence is a lie in two directions — the key panel is hidden there, and a key
/// pasted into a browser is ignored by design — so a visitor could hold the button, say a whole
/// sentence, and be told to go do an impossible thing. The demo box going managed with no ElevenLabs
/// key (2026-09-21) turns that from a corner into the default experience.</para>
///
/// <para>So the predicate and the copy live here together and every site asks: the mic affordances,
/// the hands-free reader, and the transcriber's own failure path. Per CLAUDE.md, a fact used in more
/// than one place gets one accessible definition — and converting the sites one at a time would be
/// worse than not starting, because a half-converted state is a button that promises what its
/// neighbour then refuses.</para>
/// </summary>
public static class VoiceEar
{
    /// <summary>The state of the ear, from the two things that decide it: whether the host's keys are
    /// authoritative on this deployment, and whether there is a key at all.</summary>
    public static EarAvailability Availability(bool managed, string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) ? EarAvailability.Ready
            : managed ? EarAvailability.NotOnThisBox
            : EarAvailability.NeedsVisitorKey;

    /// <summary>Whether to offer a microphone at all. A control that cannot do its job is not offered;
    /// the same rule that gated the cook-along's <c>go_to_step</c> on a real step count.</summary>
    public static bool CanHear(this EarAvailability availability) => availability == EarAvailability.Ready;

    /// <summary>What to tell someone when the ear is shut, or null when it isn't. One sentence, written
    /// for a person, and DIFFERENT for the two reasons: one is an instruction the reader can act on, the
    /// other is a fact about the deployment. Never the instruction for both — that was the bug.</summary>
    public static string? WhyNot(this EarAvailability availability) => availability switch
    {
        EarAvailability.NeedsVisitorKey => "Add your ElevenLabs key in Settings to use voice.",
        EarAvailability.NotOnThisBox => "Voice input isn't available on this box.",
        _ => null,
    };
}
