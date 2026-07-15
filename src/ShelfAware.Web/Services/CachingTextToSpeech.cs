using System.Security.Cryptography;
using System.Text;
using ShelfAware.Core.Speech;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>
/// Caches synthesized audio on disk, keyed by what actually determines the sound: the text, the
/// neighbouring segments, and the synthesizer's <see cref="ITextToSpeech.OutputFingerprint"/>.
///
/// Recipe steps are static text. Without this, every re-read of the same recipe re-synthesized every
/// step and paid for it again — and the reader waited on the network to say a sentence it had already
/// said yesterday. With it, a recipe costs one synthesis ever and re-opens instantly.
///
/// **Scoped to one household.** It was briefly content-addressed and SHARED, on the theory that a hit
/// requires identical text so there's nothing to learn from one — and that a keyless visitor could then
/// hear seeded/demo recipes someone else had paid to synthesize. That second half was the only thing the
/// sharing actually bought, and it never worked: on a keyless deploy nobody has a key, so nobody ever
/// warms the cache, so it never pays out. It was risk with no realised benefit. Households essentially
/// never share step text anyway (recipes are generated per household), so the sharing bought nothing
/// while making one household's audio outlive its owner — "delete my data" cannot reach a clip it
/// cannot identify. Per-household directories are deletable. If keyless demo audio is ever wanted, the
/// answer is to PRE-WARM the demo household's cache deliberately, not to leave every household's audio
/// readable by hash.
///
/// Cache failures are never synthesis failures: if the disk misbehaves we log it and go to the provider.
/// </summary>
public sealed class CachingTextToSpeech : ITextToSpeech
{
    private readonly ITextToSpeech _inner;
    private readonly string _root;
    private readonly ICurrentHousehold _household;
    private readonly ILogger<CachingTextToSpeech> _logger;

    public CachingTextToSpeech(
        ITextToSpeech inner, string root, ICurrentHousehold household, ILogger<CachingTextToSpeech> logger)
    {
        _inner = inner;
        _root = root;
        _household = household;
        _logger = logger;
    }

    /// <summary>
    /// Forget everything ever spoken for one household — its recipes' audio is a recording of its content,
    /// so "delete my data" has to reach it or it isn't true. Exposed as an operation rather than exposing
    /// the folder layout: the caller shouldn't have to know how clips are filed to be allowed to delete
    /// them. Returns false if something was there and wouldn't go.
    /// </summary>
    public static bool DeleteHousehold(string root, string householdId, ILogger logger)
    {
        var folder = Path.Combine(root, HouseholdFolder.For(householdId));
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't remove cached speech at {Folder}.", folder);
            return false;
        }
    }

    public string OutputFingerprint => _inner.OutputFingerprint;
    public string OutputMediaType => _inner.OutputMediaType;

    public async Task<TextToSpeechResult> SynthesizeAsync(
        string text, SpeechContext? context = null, CancellationToken cancellationToken = default)
    {
        // Blank text isn't ours to rule on — let the provider own that (and its error message).
        if (string.IsNullOrWhiteSpace(text)) return await _inner.SynthesizeAsync(text, context, cancellationToken);

        // No household, no cache. An unauthenticated scope has no drawer of its own to read from or write
        // to, and guessing one would be exactly the cross-household sharing this is scoped to avoid.
        var householdId = await _household.GetIdAsync(cancellationToken);
        if (householdId is null) return await _inner.SynthesizeAsync(text, context, cancellationToken);

        var directory = Path.Combine(_root, HouseholdFolder.For(householdId));
        var path = Path.Combine(directory, KeyFor(text, context) + ".audio");

        if (await TryReadAsync(path, cancellationToken) is { } cached)
        {
            _logger.LogDebug("Serving {Bytes} cached byte(s) of speech.", cached.Length);
            return TextToSpeechResult.Ok(cached, _inner.OutputMediaType);
        }

        var result = await _inner.SynthesizeAsync(text, context, cancellationToken);
        if (result.Success) await TryWriteAsync(directory, path, result.Audio, cancellationToken);
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

    private async Task TryWriteAsync(string directory, string path, byte[] audio, CancellationToken cancellationToken)
    {
        // Write-then-move so a concurrent reader never sees a half-written clip, and two circuits
        // synthesizing the same step race harmlessly to publish identical bytes. The temp file shares the
        // household's directory so the move is a rename within one volume, never a copy across one.
        var temp = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
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
    /// Trims the cache, oldest clip first. Called once at startup rather than on every write: the cache
    /// only grows when text changes (an edited step orphans its clip, and its neighbours'), so it creeps
    /// rather than spikes, and a per-write sweep would cost a directory scan on the path we just made
    /// fast. Returns the number of files removed.
    ///
    /// <paramref name="maxBytesPerHousehold"/> is a budget PER HOUSEHOLD, and each household's folder is
    /// swept against its own. One shared budget over the whole tree made the sweep cross-tenant in the one
    /// way that still mattered after the clips themselves were separated: the oldest clips anywhere got
    /// deleted, so a household that cooks a lot would silently evict the recipes of one that doesn't, and
    /// that household would then pay to re-synthesize audio it had already bought. Whose clips go should
    /// depend on your own usage, not your neighbour's.
    ///
    /// The cost is that total disk is now households × budget rather than a single ceiling. That's the
    /// honest shape of the promise — "your recipes stay cached" can't be kept from a shared pot — and on a
    /// self-host with one or two households it's the same number it always was.
    /// </summary>
    public static int Trim(string directory, long maxBytesPerHousehold, ILogger logger)
    {
        if (!Directory.Exists(directory)) return 0;

        // Each immediate subfolder is one household (see HouseholdFolder). Clips written before the cache
        // was split by household sit loose at the root; sweeping that too keeps them from lingering
        // forever, and it's the only thing left that can't be attributed.
        var households = new DirectoryInfo(directory).GetDirectories();
        var removed = TrimFolder(directory, maxBytesPerHousehold, SearchOption.TopDirectoryOnly, logger);
        foreach (var household in households)
        {
            removed += TrimFolder(household.FullName, maxBytesPerHousehold, SearchOption.AllDirectories, logger);
        }
        return removed;
    }

    private static int TrimFolder(string directory, long maxBytes, SearchOption depth, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(directory)) return 0;

            var files = new DirectoryInfo(directory).GetFiles("*.audio", depth);
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

            logger.LogInformation(
                "Trimmed {Removed} cached speech file(s) in {Directory}; now {Bytes} byte(s).",
                removed, directory, total);
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // One household's unreadable folder must not stop the others being swept.
            logger.LogWarning(ex, "Couldn't trim the speech cache at {Directory}.", directory);
            return 0;
        }
    }
}
