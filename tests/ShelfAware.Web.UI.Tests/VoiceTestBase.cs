using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Speech;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// Harness for the voice components: the speech seams are fakes (the REAL implementations are
/// pinned in ShelfAware.Llm.Tests), the browser side is bUnit's JS interop scripted per test, and
/// the page-harness stubs are cleared — here the voice components ARE the subject. The one
/// convention that keeps loop tests deterministic: the fake capture is STICKY (every listening
/// window hears the same bytes) and <see cref="FakeSpeechToText"/> is the sequencer, with an
/// exhausted queue answering "stop listening" so a loop always winds down.
/// </summary>
public abstract class VoiceTestBase : PageTestContext
{
    internal FakeSpeechToText Stt { get; } = new();
    internal FakeTextToSpeech Tts { get; } = new();

    protected override void RegisterAdditionalServices()
    {
        ComponentFactories.Clear(); // the voice components are stubbed for PAGE tests; not here
        Services.AddSingleton<ISpeechToText>(Stt);
        Services.AddSingleton<ITextToSpeech>(Tts);
    }

    private protected static string OneByteBase64 => Convert.ToBase64String(new byte[] { 1 });
}
