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
// A clip that loaded, ran and said nothing is caught in KokoroTextToSpeech itself (an empty
// synthesis is a failure, not a very short clip), so reaching this far already means there
// is audio -- the tool's job is to let a person hear whether it is the RIGHT audio.
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
var speakerId = 0;
if (args.Length > 2 && !int.TryParse(args[2], out speakerId))
{
    Console.Error.WriteLine($"'{args[2]}' is not a voice index. The archive ships no voice names, so a voice "
                            + "is a number -- 0 to 10 for kokoro-int8-en-v0_19.");
    return 1;
}

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

await File.WriteAllBytesAsync(outputPath, result.Audio);

Console.WriteLine();
Console.WriteLine($"Wrote {result.Audio.Length:N0} bytes of {result.MediaType} to {Path.GetFullPath(outputPath)}");
Console.WriteLine($"Took {started.Elapsed.TotalSeconds:F1}s including the one-off model load.");

// Read the duration back out of the header rather than dividing by a rate typed in here: the model reports
// its own sample rate (the log line above says it), and two places computing the same number from
// different sources is how one of them ends up describing a clip the other didn't produce.
var (seconds, rate) = DurationOf(result.Audio);
Console.WriteLine($"Roughly {seconds:F1}s of audio at {rate} Hz. Play it: the voice should read that "
                  + "sentence, with \"350 degrees Fahrenheit\" spelled out.");
return 0;

// Length and rate straight off the WAV header, the way a player reads them.
static (double Seconds, int Rate) DurationOf(byte[] wav)
{
    var rate = BitConverter.ToInt32(wav, 24);
    var bytesPerSecond = BitConverter.ToInt32(wav, 28);
    var dataBytes = BitConverter.ToInt32(wav, 40);
    return (dataBytes / (double)bytesPerSecond, rate);
}
