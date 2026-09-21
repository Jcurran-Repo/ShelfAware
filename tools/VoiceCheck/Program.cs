using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ShelfAware.Core.Speech;
using ShelfAware.Llm;

// Proves a box's local voice model actually speaks, through the SAME code the app uses --
// SherpaTtsEngine loading the model and SherpaTextToSpeech turning a recipe step into playable
// bytes. This is the in-process shape's equivalent of the curl smoke test the HTTP sidecar had,
// and it exists for the same reason: the first read-aloud on a live box should not be the first
// time the model has been loaded there.
//
//   dotnet run --project tools/VoiceCheck -- <kokoro|piper> <modelDirectory> [outputWav] [speakerId]
//
// ⚠️ Run it BEFORE pointing Speech:Provider at a model directory. sherpa-onnx answers a missing
// model file with a line on stderr and a SIGSEGV, so a box configured against an incomplete
// directory boots clean and then dies whole on the first read-aloud.
//
// A clip that loaded, ran and said nothing is caught in SherpaTextToSpeech itself (an empty
// synthesis is a failure, not a very short clip), so reaching this far already means there
// is audio -- the tool's job is to let a person hear whether it is the RIGHT audio.
//
// It is deliberately NOT a unit test. The models are 60-150 MB and are not in the repo, so a test
// would either fail on every machine that hasn't downloaded them or -- worse -- skip itself
// quietly and report a green nobody earned.

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tools/VoiceCheck -- <kokoro|piper> <modelDirectory> [outputWav] [speakerId]");
    Console.Error.WriteLine(
        "The model directory holds an unpacked sherpa-onnx archive -- see docs/deploy-kokoro.md or "
        + "docs/deploy-piper.md.");
    return 1;
}

var family = args[0].Trim();
var modelDirectory = args[1];
var outputPath = args.Length > 2 ? args[2] : "voice-check.wav";
var speakerId = 0;
if (args.Length > 3 && !int.TryParse(args[3], out speakerId))
{
    Console.Error.WriteLine($"'{args[3]}' is not a voice index. The archives ship no voice names, so a "
                            + "voice is a number -- 0 to 10 for kokoro-int8-en-v0_19, and 0 for a "
                            + "single-speaker Piper voice.");
    return 1;
}

// The family decides which settings object is built, and everything after this line is family-agnostic
// -- the same seam the app itself runs on, so a model this tool accepts is one the app can load.
SherpaTtsOptions options;
switch (family.ToLowerInvariant())
{
    case "kokoro":
        options = new KokoroSpeechOptions { ModelDirectory = modelDirectory, SpeakerId = speakerId };
        break;
    case "piper":
        // Piper names its weights after the voice, and an archive holds exactly one .onnx, so the file
        // is discovered rather than typed -- a person running this tool has just unpacked a directory,
        // not memorised what is in it. A directory with two would be ambiguous, so it says so.
        var onnx = Directory.Exists(modelDirectory)
            ? Directory.GetFiles(modelDirectory, "*.onnx")
            : [];
        if (onnx.Length != 1)
        {
            Console.Error.WriteLine(onnx.Length == 0
                ? $"No .onnx file in '{modelDirectory}' -- that is not an unpacked Piper archive. "
                  + "See docs/deploy-piper.md."
                : $"'{modelDirectory}' holds {onnx.Length} .onnx files, so this tool cannot tell which "
                  + "voice you mean. Point it at a directory holding one archive.");
            return 1;
        }
        options = new PiperSpeechOptions
        {
            ModelDirectory = modelDirectory,
            ModelFile = Path.GetFileName(onnx[0]),
            SpeakerId = speakerId,
        };
        break;
    default:
        Console.Error.WriteLine($"'{family}' is not a model family. Use 'kokoro' or 'piper'.");
        return 1;
}

// The same checks registration runs at startup, run here first so a missing file or a bad setting is a
// sentence rather than a SIGSEGV -- the native library kills the process on a bad path with nothing of
// ours in the log.
if (options.Invalid() is { } wrong)
{
    Console.Error.WriteLine(wrong);
    return 1;
}

if (options.Model().Missing() is { Count: > 0 } missing)
{
    Console.Error.WriteLine($"That is not a complete {family} model -- not found: {string.Join(", ", missing)}");
    Console.Error.WriteLine("See docs/deploy-kokoro.md or docs/deploy-piper.md for the archive to unpack there.");
    return 1;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

using var engine = new SherpaTtsEngine(options, loggerFactory.CreateLogger<SherpaTtsEngine>());
ITextToSpeech tts = new SherpaTextToSpeech(engine, options, loggerFactory.CreateLogger<SherpaTextToSpeech>());

// Numbers and a unit abbreviation on purpose: this sentence also shows whether SpeechText's spelling-out
// is reaching the model, which is the difference between "350 degrees Fahrenheit" and "350 °F".
const string Line = "Shelf Aware is talking. Sear the chicken 6-7 min/side, then roast at 350°F.";

Console.WriteLine($"Family:      {options.Family}");
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

// Read the duration back out of the CLIP rather than dividing by a rate typed in here: the model reports
// its own sample rate (the log line above says it), and two places computing the same number from
// different sources is how one of them ends up describing a clip the other didn't produce. Through
// WaveAudio's own decoder, which also means a clip this tool describes is provably one the app can read
// back -- the ear (tools/MoonshineCheck) opens it with exactly this call.
var decoded = WaveAudio.Decode(result.Audio);
var seconds = decoded.Samples.Length / (double)decoded.SampleRate;
var rate = decoded.SampleRate;
Console.WriteLine($"Roughly {seconds:F1}s of audio at {rate} Hz. Play it: the voice should read that "
                  + "sentence, with \"350 degrees Fahrenheit\" spelled out.");

// ⚠️ The rate is the number that decides whether streaming playback can keep up: below 1.0 synthesis
// outruns speech, above it every sentence arrives later than the one before it finished. Printed here
// because it is measured per BOX -- the same model is 0.05x on one core and 3x on another.
var rateOfRealTime = started.Elapsed.TotalSeconds / seconds;
Console.WriteLine($"That is roughly {rateOfRealTime:F2}x real time on this box, model load included.");
return 0;
