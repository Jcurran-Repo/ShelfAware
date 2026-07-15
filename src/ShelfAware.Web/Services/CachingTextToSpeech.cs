using System.Security.Cryptography;
using System.Text;
using ShelfAware.Core.Speech;

namespace ShelfAware.Web.Services;

/// <summary>
/// Caches synthesized audio on disk, keyed by what actually determines the sound: the text, the
/// neighbouring segments, and the synthesizer's <see cref="ITextToSpeech.OutputFingerprint"/>.
///
/// Recipe steps are static text. Without this, every re-read of the same recipe re-synthesized every
/// step and paid for it again — and the reader waited on the network to say a sentence it had already
/// said yesterday. With it, a recipe costs one synthesis ever and re-opens instantly.
///
/// The cache is CONTENT-addressed and shared across households. That is deliberate: a hit requires the
/// identical text, which means the asker already has the content, so there is nothing to learn from a
/// hit that they didn't already know. It also means a hit needs no API key at all — which is what lets
/// seeded/demo recipes talk for a visitor who has brought no key of their own.
///
/// Cache failures are never synthesis failures: if the disk misbehaves we log it and go to the provider.
/// </summary>
public sealed class CachingTextToSpeech : ITextToSpeech
{
    private readonly ITextToSpeech _inner;
    private readonly string _directory;
    private readonly ILogger<CachingTextToSpeech> _logger;

    public CachingTextToSpeech(ITextToSpeech inner, string directory, ILogger<CachingTextToSpeech> logger)
    {
        _inner = inner;
        _directory = directory;
        _logger = logger;
    }

    public string OutputFingerprint => _inner.OutputFingerprint;
    public string OutputMediaType => _inner.OutputMediaType;

    public async Task<TextToSpeechResult> SynthesizeAsync(
        string text, SpeechContext? context = null, CancellationToken cancellationToken = default)
    {
        // Blank text isn't ours to rule on — let the provider own that (and its error message).
        if (string.IsNullOrWhiteSpace(text)) return await _inner.SynthesizeAsync(text, context, cancellationToken);

        var path = Path.Combine(_directory, KeyFor(text, context) + ".audio");

        if (await TryReadAsync(path, cancellationToken) is { } cached)
        {
            _logger.LogDebug("Serving {Bytes} cached byte(s) of speech.", cached.Length);
            return TextToSpeechResult.Ok(cached, _inner.OutputMediaType);
        }

        var result = await _inner.SynthesizeAsync(text, context, cancellationToken);
        if (result.Success) await TryWriteAsync(path, result.Audio, cancellationToken);
        return result;
    }

    /// <summary>
    /// The neighbouring segments are part of the key because they change the audio — they're sent as
    /// continuity hints, so the same sentence read after a different one genuinely sounds different.
    /// Keying on them costs a little re-synthesis when a step is edited (its neighbours' clips retire
    /// too) and buys a cache that can't serve a clip voiced for a different position in the recipe.
    /// </summary>
    private string KeyFor(string text, SpeechContext? context)
    {
        // Length-prefixed so no combination of texts can collide by shifting the delimiter.
        var sb = new StringBuilder();
        Append(sb, _inner.OutputFingerprint);
        Append(sb, text);
        Append(sb, context?.Previous);
        Append(sb, context?.Next);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);

        static void Append(StringBuilder sb, string? part) =>
            sb.Append(part?.Length ?? -1).Append(':').Append(part).Append('|');
    }

    private async Task<byte[]?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache we can't read is just a cache miss.
            _logger.LogWarning(ex, "Couldn't read cached speech from {Path}; synthesizing instead.", path);
            return null;
        }
    }

    private async Task TryWriteAsync(string path, byte[] audio, CancellationToken cancellationToken)
    {
        // Write-then-move so a concurrent reader never sees a half-written clip, and two circuits
        // synthesizing the same step race harmlessly to publish identical bytes.
        var temp = Path.Combine(_directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(temp, audio, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CleanUp(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache we can't write to costs money, not correctness — say so and carry on.
            _logger.LogWarning(ex, "Couldn't cache synthesized speech at {Path}.", path);
            CleanUp(temp);
        }
    }

    private void CleanUp(string temp)
    {
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't remove the temporary speech file {Path}.", temp);
        }
    }

    /// <summary>
    /// Trims the cache to <paramref name="maxBytes"/>, oldest clip first. Called once at startup rather
    /// than on every write: the cache only grows when text changes (an edited step orphans its clip, and
    /// its neighbours'), so it creeps rather than spikes, and a per-write sweep would cost a directory
    /// scan on the path we just made fast. Returns the number of files removed.
    /// </summary>
    public static int Trim(string directory, long maxBytes, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(directory)) return 0;

            var files = new DirectoryInfo(directory).GetFiles("*.audio");
            var total = files.Sum(f => f.Length);
            if (total <= maxBytes) return 0;

            var removed = 0;
            // LastWriteTime, not LastAccessTime — NTFS doesn't reliably maintain access times, so an
            // LRU here would be a lie. Oldest-written is honest and good enough for orphaned clips.
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= maxBytes) break;
                var size = file.Length;
                try
                {
                    file.Delete();
                    total -= size;
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Couldn't trim cached speech file {Path}.", file.FullName);
                }
            }

            logger.LogInformation("Trimmed {Removed} cached speech file(s); cache now {Bytes} byte(s).", removed, total);
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't trim the speech cache at {Directory}.", directory);
            return 0;
        }
    }
}
