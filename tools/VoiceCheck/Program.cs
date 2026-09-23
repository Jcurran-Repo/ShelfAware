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
//   dotnet run --project tools/VoiceCheck -- <kokoro|piper|matcha|kitten> <modelDirectory>
//                                           [outputWav] [speakerId]
//                                           [--model-file <name.onnx>] [--vocoder-file <name.onnx>]
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
        "Usage: dotnet run --project tools/VoiceCheck -- <kokoro|piper|matcha|kitten> <modelDirectory> "
        + "[outputWav] [speakerId] [--model-file <name.onnx>] [--vocoder-file <name.onnx>]");
    Console.Error.WriteLine(
        "The model directory holds an unpacked sherpa-onnx archive -- see docs/deploy-kokoro.md, "
        + "docs/deploy-piper.md or docs/voice-bakeoff.md.");
    return 1;
}

// --model-file is lifted out BEFORE the positional arguments are read, so it can never be mistaken
// for the voice index and so the Piper check below sees an override the operator has just applied.
string? modelFileOverride = null;
string? vocoderFileOverride = null;
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--model-file" && i + 1 < args.Length) { modelFileOverride = args[++i]; continue; }
    if (args[i] == "--vocoder-file" && i + 1 < args.Length) { vocoderFileOverride = args[++i]; continue; }
    positional.Add(args[i]);
}
args = [.. positional];

var family = args[0].Trim();
var modelDirectory = args[1];
var outputPath = args.Length > 2 ? args[2] : "voice-check.wav";
var speakerId = 0;
if (args.Length > 3 && !int.TryParse(args[3], out speakerId))
{
    Console.Error.WriteLine($"'{args[3]}' is not a voice index. The archives ship no voice names, so a "
                            + "voice is a number -- 0 to 10 for kokoro-int8-en-v0_19, 0 to 7 for "
                            + "kitten-nano-en-v0_1, and 0 for a single-speaker Piper or Matcha voice.");
    return 1;
}

// ⚠️ Only Matcha has a vocoder. Accepting the flag for the others and ignoring it would be this tool
// checking something other than what the operator asked for, which is the one thing a pre-flight check
// must never do.
if (vocoderFileOverride is not null && !string.Equals(family, "matcha", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        $"--vocoder-file means nothing to '{family}'. Only matcha has a separate vocoder; the other "
        + "families carry everything they need in one archive.");
    return 1;
}

// The family decides which settings object is built, and everything after this line is family-agnostic
// -- the same seam the app itself runs on, so a model this tool accepts is one the app can load.
SherpaTtsOptions options;
switch (family.ToLowerInvariant())
{
    case "kokoro":
        // --model-file honoured here too: docs/deploy-kokoro.md and deploy/env.example both document
        // Speech__Kokoro__ModelFile=model.onnx for the full-precision archive, so a tool that ignored
        // the override would pre-flight model.int8.onnx and report a voice the box is not configured
        // to run -- the exact substitution the piper case below exists to refuse.
        options = new KokoroSpeechOptions { ModelDirectory = modelDirectory, SpeakerId = speakerId };
        if (modelFileOverride is not null) options.ModelFileSetting = modelFileOverride;
        break;
    case "piper":
        // The app's OWN rule for the weights' name, not whatever .onnx happens to be in the directory:
        // with no --model-file, a vits-piper-<voice> directory resolves to <voice>.onnx exactly as it
        // does on the box, so the common case needs no flag. --model-file stays for an archive that
        // names its weights some other way -- the same case the box needs Speech__Piper__ModelFile for.
        options = new PiperSpeechOptions { ModelDirectory = modelDirectory, SpeakerId = speakerId };
        if (modelFileOverride is not null) options.ModelFileSetting = modelFileOverride;

        // ⚠️ A directory holding a DIFFERENT voice than the one being checked is the case worth
        // catching: the tool could load it happily and the app would then REFUSE to boot, which is the
        // exact outcome a pre-flight check exists to prevent. So the tool accepts only what the app
        // would accept, and says which setting line the box needs rather than quietly checking a voice
        // nobody configured. (A directory renamed away from vits-piper-<voice> lands here too.) A name
        // given explicitly BLANK is not this case -- it is a bad setting, and Invalid() below refuses it
        // by name rather than this branch misdescribing the directory.
        if (!(options.ModelFileIsSet && string.IsNullOrWhiteSpace(options.ModelFile))
            && !File.Exists(Path.Combine(modelDirectory, options.ModelFile))
            && Directory.Exists(modelDirectory)
            && Directory.GetFiles(modelDirectory, "*.onnx") is [var only])
        {
            var name = Path.GetFileName(only);
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(options.ModelFile)
                ? $"'{modelDirectory}' holds {name}, and its name is not {PiperSpeechOptions.ArchivePrefix}<voice>, "
                  + "so the app cannot work out which weights to load -- add this to the box's environment:"
                : $"'{modelDirectory}' holds {name}, not {options.ModelFile}. That is a different voice, so "
                  + "the app needs telling -- add this to the box's environment:");
            Console.Error.WriteLine($"  Speech__Piper__ModelFile={name}");
            Console.Error.WriteLine($"and re-run this with: --model-file {name}");
            return 1;
        }
        break;
    case "matcha":
        // ⚠️ Matcha is an acoustic model plus a VOCODER, and the vocoder ships in a different release
        // from the voice -- so a directory holding everything the voice archive contained is still not
        // something that can speak. Both names are overridable because neither is fixed: the voice
        // archives name the acoustic model after its step count, and the vocoder is whichever of the
        // published builds was downloaded beside it.
        var matcha = new MatchaSpeechOptions { ModelDirectory = modelDirectory, SpeakerId = speakerId };
        if (vocoderFileOverride is not null) matcha.VocoderFile = vocoderFileOverride;
        options = matcha;
        if (modelFileOverride is not null) options.ModelFileSetting = modelFileOverride;
        break;
    case "kitten":
        options = new KittenSpeechOptions { ModelDirectory = modelDirectory, SpeakerId = speakerId };
        if (modelFileOverride is not null) options.ModelFileSetting = modelFileOverride;
        break;
    default:
        Console.Error.WriteLine(
            $"'{family}' is not a model family. Use 'kokoro', 'piper', 'matcha' or 'kitten'.");
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
    Console.Error.WriteLine($"That is not a complete {family} model -- {options.DescribeMissing(missing)}");
    Console.Error.WriteLine(
        "See docs/deploy-kokoro.md, docs/deploy-piper.md or docs/voice-bakeoff.md for the archive to "
        + "unpack there.");
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
Console.WriteLine($"Took {started.Elapsed.TotalSeconds:F1}s, including the one-off model load.");

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

// ⚠️ The rate is the number that decides whether playback can keep up: below 1.0 synthesis outruns
// speech, above it every sentence arrives later than the one before it finished. Printed here because
// it is measured per BOX -- the same model is 0.05x on one core and 3x on another.
//
// ⚠️ And it is measured on a SECOND read, once the model is loaded. The app loads its model once per
// process, so the load is not what a household waits for on a read-aloud -- but on a clip this short it
// is most of the first read's time. This tool used to report only that first read, and it made the
// droplet's Piper voices look alike: ryan-high and lessac-medium read 1.43x and 0.66x there, which
// hid that ryan-high's synthesis alone is 0.81-0.95x against lessac's 0.13-0.19x -- five times the
// cost, right at the line. The first read is still printed, because the first read-aloud after a
// restart does pay it -- and printed BEFORE the second read, so a second read that fails does not take
// the first one's number with it.
Console.WriteLine($"The first read, which also loaded the model, took {started.Elapsed.TotalSeconds / seconds:F2}x "
                  + "its own length -- what the first read-aloud after a restart costs.");

var steady = Stopwatch.StartNew();
var again = await tts.SynthesizeAsync(Line);
steady.Stop();

if (!again.Success)
{
    Console.Error.WriteLine($"The second read failed: {again.Error}");
    Console.Error.WriteLine("The log line above says which failure it was.");
    return 1;
}

var againDecoded = WaveAudio.Decode(again.Audio);
var againSeconds = againDecoded.Samples.Length / (double)againDecoded.SampleRate;
Console.WriteLine($"Once loaded, that is roughly {steady.Elapsed.TotalSeconds / againSeconds:F2}x real time on this "
                  + "box -- the number to hold against 1.0.");
return 0;
