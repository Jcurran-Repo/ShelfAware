using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Speech;
using ShelfAware.Llm;

// Proves a box's Moonshine model actually HEARS, through the SAME code the app uses --
// SherpaMoonshineEngine loading the model and MoonshineSpeechToText turning a WAV into text. The ear's
// counterpart to tools/VoiceCheck, and it exists for the same reason: the first spoken word on a live
// box should not be the first time the model has been loaded there.
//
//   dotnet run --project tools/MoonshineCheck -- <modelDirectory> <wavFile> [more.wav ...]
//
// Give it a 16 kHz mono WAV -- which is exactly what the browser now sends (wwwroot/js/pcm.js), so a
// clip captured from the app is the most honest input you can hand this. VoiceCheck's output is one
// too, which makes "say something, then hear it back" a two-command test of the whole voice stack.
//
// It is deliberately NOT a unit test. The model is 120 MB and is not in the repo, so a test would either
// fail on every machine that hasn't downloaded it or -- worse -- skip itself quietly and report a green
// nobody earned.

var modelDirectory = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("Speech__Moonshine__ModelDirectory");
if (string.IsNullOrWhiteSpace(modelDirectory) || args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/MoonshineCheck -- <modelDirectory> <wavFile> [more.wav ...]");
    Console.Error.WriteLine("The model directory holds an unpacked sherpa-onnx Moonshine archive (see docs/deploy-moonshine.md).");
    Console.Error.WriteLine("The WAV should be 16 kHz mono -- tools/VoiceCheck writes one, and so does the app's own capture.");
    return 1;
}

var options = new MoonshineSpeechOptions { ModelDirectory = modelDirectory };

// The same check registration runs at startup, run here first so a missing file is a sentence rather
// than a SIGSEGV -- the native library kills the process on a bad path with nothing of ours in the log.
if (options.Invalid() is { } wrong)
{
    Console.Error.WriteLine(wrong);
    return 1;
}

var files = MoonshineModelFiles.In(options.ModelDirectory);
if (files.Missing() is { Count: > 0 } missing)
{
    Console.Error.WriteLine($"That is not a complete Moonshine model -- not found: {string.Join(", ", missing)}");
    Console.Error.WriteLine("See docs/deploy-moonshine.md for the archive to unpack there.");
    return 1;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

using var engine = new SherpaMoonshineEngine(Options.Create(options), loggerFactory.CreateLogger<SherpaMoonshineEngine>());
ISpeechToText ear = new MoonshineSpeechToText(engine, loggerFactory.CreateLogger<MoonshineSpeechToText>());

Console.WriteLine($"Model:   {modelDirectory}");
Console.WriteLine($"Threads: {options.NumThreads}");
Console.WriteLine();

var failed = false;
foreach (var path in args[1..])
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"No such file: {path}");
        failed = true;
        continue;
    }

    var bytes = await File.ReadAllBytesAsync(path);

    // Read the duration through the app's own decoder rather than off the header here: two places
    // computing the same number from the same bytes is how one of them ends up describing a clip the
    // other didn't read. It also means a WAV this tool reports on is provably one the EAR can open.
    double seconds;
    int rate;
    try
    {
        var (samples, sampleRate) = WaveAudio.Decode(bytes);
        seconds = samples.Length / (double)sampleRate;
        rate = sampleRate;
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine($"{Path.GetFileName(path)}: {ex.Message}");
        failed = true;
        continue;
    }

    var started = Stopwatch.StartNew();
    var result = await ear.TranscribeAsync(new AudioClip(bytes, WaveAudio.MediaType));
    started.Stop();

    if (!result.Success)
    {
        Console.Error.WriteLine($"{Path.GetFileName(path)}: transcription failed -- {result.Error}");
        Console.Error.WriteLine("The log line above says which failure it was.");
        failed = true;
        continue;
    }

    var realTime = started.Elapsed.TotalSeconds / Math.Max(seconds, 0.001);
    Console.WriteLine(
        $"  {Path.GetFileName(path),-24} {seconds,5:F2}s at {rate} Hz -> {started.ElapsedMilliseconds,5} ms "
        + $"({realTime:F2}x real time)");
    Console.WriteLine($"  {"",-24} \"{result.Text}\"");
}

Console.WriteLine();
Console.WriteLine(failed
    ? "Something didn't read. The lines above say which."
    : "Read every clip. The first timing includes the one-off model load; the rest are what a spoken "
      + "command actually costs on this box.");
return failed ? 1 : 0;
