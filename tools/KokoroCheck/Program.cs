using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;
using ShelfAware.Llm;

// Proves a box's Kokoro model actually speaks, through the SAME code the app uses --
// SherpaKokoroEngine loading the model and KokoroTextToSpeech turning a recipe step into
// playable bytes. This is the in-process shape's equivalent of the curl smoke test the
// HTTP sidecar had, and it exists for the same reason: the first read-aloud on a live box
// should not be the first time the model has been loaded there.
//
//   dotnet run --project tools/KokoroCheck -- <modelDirectory> [outputWav] [speakerId]
//
// It is deliberately NOT a unit test. The model is 150 MB and is not in the repo, so a test
// would either fail on every machine that hasn't downloaded it or -- worse -- skip itself
// quietly and report a green nobody earned.

var modelDirectory = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("Speech__Kokoro__ModelDirectory");
if (string.IsNullOrWhiteSpace(modelDirectory))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/KokoroCheck -- <modelDirectory> [outputWav] [speakerId]");
    Console.Error.WriteLine("The model directory holds an unpacked sherpa-onnx Kokoro archive (see docs/deploy-kokoro.md).");
    return 1;
}

var outputPath = args.Length > 1 ? args[1] : "kokoro-check.wav";
var speakerId = args.Length > 2 ? int.Parse(args[2]) : 0;

var options = new KokoroSpeechOptions { ModelDirectory = modelDirectory, SpeakerId = speakerId };

// The same check registration runs at startup, run here first so a missing file is a sentence rather
// than a SIGSEGV -- the native library kills the process on a bad path with nothing of ours in the log.
var files = KokoroModelFiles.In(options.ModelDirectory, options.ModelFile);
if (files.Missing() is { Count: > 0 } missing)
{
    Console.Error.WriteLine($"That is not a complete Kokoro model -- not found: {string.Join(", ", missing)}");
    Console.Error.WriteLine("See docs/deploy-kokoro.md for the archive to unpack there.");
    return 1;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

using var engine = new SherpaKokoroEngine(Options.Create(options), loggerFactory.CreateLogger<SherpaKokoroEngine>());
ITextToSpeech tts = new KokoroTextToSpeech(engine, Options.Create(options), loggerFactory.CreateLogger<KokoroTextToSpeech>());

// Numbers and a unit abbreviation on purpose: this sentence also shows whether SpeechText's spelling-out
// is reaching the model, which is the difference between "350 degrees Fahrenheit" and "350 °F".
const string Line = "Shelf Aware is talking. Sear the chicken 6-7 min/side, then roast at 350°F.";

Console.WriteLine($"Model:       {modelDirectory}");
Console.WriteLine($"Voice:       {speakerId}");
Console.WriteLine($"Fingerprint: {tts.OutputFingerprint}");
Console.WriteLine($"Saying:      \"{Line}\"");

var started = Stopwatch.StartNew();
var result = await tts.SynthesizeAsync(Line);
started.Stop();

if (!result.Success)
{
    Console.Error.WriteLine($"Synthesis failed: {result.Error}");
    Console.Error.WriteLine("The log line above says which failure it was.");
    return 1;
}

// 44-byte header, 16-bit mono: enough to report what a player will find, without a decoder.
var seconds = (result.Audio.Length - 44) / 2.0 / 24000;
Console.WriteLine();
Console.WriteLine($"Wrote {result.Audio.Length:N0} bytes of {result.MediaType} to {Path.GetFullPath(outputPath)}");
Console.WriteLine($"Took {started.Elapsed.TotalSeconds:F1}s including the one-off model load.");
await File.WriteAllBytesAsync(outputPath, result.Audio);

// A clip that is the right length but silent is the failure this catches -- a model that loaded, ran, and
// said nothing looks like success in every number above.
if (result.Audio.Length <= 44)
{
    Console.Error.WriteLine("...but the clip has no samples in it. Something is wrong with the model.");
    return 1;
}

Console.WriteLine($"Roughly {seconds:F1}s of audio. Play it: the voice should read that sentence, with "
                  + "\"350 degrees Fahrenheit\" spelled out.");
return 0;
