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
/// **Kept under budget as it grows, not just at boot.** Each household's drawer is swept against
/// <c>Speech:CacheMegabytes</c> at startup (<see cref="Trim"/>) and again after any write that takes it
/// over budget. The after-write check costs the request one integer add and a compare against a running
/// total in <see cref="SpeechCacheBudget"/> — not a directory scan; the disk is only read when a
/// household first writes after boot (to seed its total) and inside the trim itself, which runs detached
/// from the request on its own thread, one at a time per household. That was the cost the once-at-boot
/// cadence was chosen to avoid, back when every clip was a small MP3 someone had paid for; the in-process
/// voices write WAV at ~48 KB per spoken second for nothing, so a cookbook read on a box that is up for
/// weeks ran far past the cap between restarts.
///
/// Cache failures are never synthesis failures: if the disk misbehaves we log it and go to the provider.
/// </summary>
public sealed class CachingTextToSpeech : ITextToSpeech, ISpeechCache
{
    private readonly ITextToSpeech _inner;
    private readonly string _root;
    private readonly SpeechCacheBudget _budget;
    private readonly ICurrentHousehold _household;
    private readonly ILogger<CachingTextToSpeech> _logger;

    public CachingTextToSpeech(
        ITextToSpeech inner,
        string root,
        SpeechCacheBudget budget,
        ICurrentHousehold household,
        ILogger<CachingTextToSpeech> logger)
    {
        _inner = inner;
        _root = root;
        _budget = budget;
        _household = household;
        _logger = logger;
    }

    /// <summary>
    /// Forget everything ever spoken for one household — its recipes' audio is a recording of its content,
    /// so "delete my data" has to reach it or it isn't true. Exposed as an operation rather than exposing
    /// the folder layout: the caller shouldn't have to know how clips are filed to be allowed to delete
    /// them. Returns false if something was there and wouldn't go.
    /// </summary>
    public bool DeleteHousehold(string householdId)
    {
        var gone = HouseholdFolder.DeleteUnder(_root, householdId, _logger);
        // The running total counted files that no longer exist; left standing it would make the next
        // write look over budget and trim an empty drawer. Only once the files really went — a total
        // for clips still on disk is still right.
        if (gone) _budget.Forget(householdId);
        return gone;
    }

    /// <summary>The same lookup <see cref="SynthesizeAsync"/> does, exposed for the export — which needs
    /// to hand a household the audio of its own recipes, and can only find a clip by asking the thing
    /// that filed it.</summary>
    public async Task<StoredClip?> FindAsync(
        string householdId, string text, SpeechContext? context = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var path = PathFor(householdId, text, context);
        return await TryReadAsync(path, cancellationToken) is { } audio
            ? new StoredClip(audio, _inner.OutputMediaType)
            : null;
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

        var path = PathFor(householdId, text, context);

        if (await TryReadAsync(path, cancellationToken) is { } cached)
        {
            _logger.LogDebug("Serving {Bytes} cached byte(s) of speech.", cached.Length);
            return TextToSpeechResult.Ok(cached, _inner.OutputMediaType);
        }

        var result = await _inner.SynthesizeAsync(text, context, cancellationToken);
        if (result.Success) await TryWriteAsync(householdId, path, result.Audio, cancellationToken);
        return result;
    }

    /// <summary>Where a clip lives. One definition, so a lookup can't drift from a write.</summary>
    private string PathFor(string householdId, string text, SpeechContext? context) =>
        Path.Combine(DrawerFor(householdId), KeyFor(text, context) + ".audio");

    /// <summary>A household's drawer — the folder its clips are filed in, and the folder its budget is
    /// measured and trimmed over. One definition, so the folder a write lands in is the folder the
    /// after-write trim sweeps.</summary>
    private string DrawerFor(string householdId) => Path.Combine(_root, HouseholdFolder.For(householdId));

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

    private async Task TryWriteAsync(string householdId, string path, byte[] audio, CancellationToken cancellationToken)
    {
        // Write-then-move so a concurrent reader never sees a half-written clip, and two circuits
        // synthesizing the same step race harmlessly to publish identical bytes. The temp file shares the
        // household's directory so the move is a rename within one volume, never a copy across one.
        var directory = DrawerFor(householdId);
        var temp = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(temp, audio, cancellationToken);
            File.Move(temp, path, overwrite: true);
            // Only once the clip is really on disk: a write that failed added nothing to the drawer.
            Charge(householdId, directory, audio.Length);
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
    /// Books a clip just written against its household's running total, and starts a trim if that took
    /// the household over budget. This is the hot path, so in the common case it is one interlocked add
    /// and one compare. The disk is read in exactly two situations: the household's FIRST write after
    /// boot, which measures the drawer once instead of adding to a total nobody has (the startup trim ran
    /// without a ledger, so what it left behind is unknown until someone looks); and the trim itself,
    /// which runs detached — the circuit that voiced the step is not kept waiting on housekeeping.
    /// </summary>
    private void Charge(string householdId, string directory, long written)
    {
        var ledger = _budget.For(householdId);
        var total = ledger.TryClaimSeed()
            ? ledger.Add(Measure(directory) ?? written)
            : ledger.Add(written);
        if (total <= _budget.MaxBytesPerHousehold) return;

        // One trim per household at a time. A write that crosses while one is in flight does not start
        // another: the running one weighs whatever is on disk when it looks, and a clip that lands after
        // that is the next crossing write's to trigger. Never two sweeps racing over the same files.
        if (!ledger.TryBeginTrim(out var snapshot)) return;

        // Detached on purpose, with no token: the request that wrote the clip may be cancelled or finished
        // long before the sweep is, and a trim that died with its circuit would leave the drawer over
        // budget with nothing scheduled to notice. Same shape as MealPlanJobs.
        ledger.Track(Task.Run(() => TrimAfterWrite(directory, ledger, snapshot)));
    }

    /// <summary>What a drawer holds right now, or null if it can't be read. The same enumeration the trim
    /// weighs (<see cref="ClipsIn"/>), so the ledger counts exactly what a sweep would.</summary>
    private long? Measure(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? BytesOf(ClipsIn(directory, SearchOption.AllDirectories)) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Counted from this write onward, as if the drawer were empty: that errs LOW, the one
            // direction the ledger otherwise never does, so the household is trimmed late rather than
            // never — the next crossing write's trim re-reads the disk and corrects it.
            _logger.LogWarning(ex, "Couldn't measure the speech cache at {Directory}; counting from this write on.", directory);
            return null;
        }
    }

    /// <summary>Where an after-write trim stops: nine-tenths of the budget, not the budget itself. A sweep
    /// to exactly the cap leaves a household one clip from crossing it again, so at steady state every
    /// synthesis would start a full scan and sort of the drawer to delete one file; the headroom spaces
    /// trims out to one per tenth of the budget written. The startup sweep still trims to the cap.</summary>
    internal static long AfterWriteTrimTarget(long maxBytesPerHousehold) => maxBytesPerHousehold / 10 * 9;

    /// <summary>The detached half of <see cref="Charge"/>: the same oldest-first sweep the startup trim
    /// runs (<see cref="TrimFolder"/>), over one household's drawer, followed by setting the ledger to
    /// what the sweep found on disk. Nothing here can reach the request that caused it — it has its own
    /// thread and no token — so nothing here can fail a synthesis, and the gate is released whatever
    /// happens, or the household could never be trimmed again until a restart.</summary>
    private void TrimAfterWrite(string directory, SpeechCacheBudget.HouseholdLedger ledger, long snapshot)
    {
        long? remaining = null;
        try
        {
            _logger.LogDebug(
                "The speech cache at {Directory} is over budget after a write ({Bytes} > {Max} byte(s)); trimming it.",
                directory, snapshot, _budget.MaxBytesPerHousehold);
            remaining = TrimFolder(directory, AfterWriteTrimTarget(_budget.MaxBytesPerHousehold), SearchOption.AllDirectories, _logger)?.Remaining;
        }
        catch (Exception ex)
        {
            // The last line of defence for a detached task: an exception that escaped here would be an
            // unobserved task fault, logged by nobody. The sweep reports the disk's own refusals itself;
            // this is for whatever it did not foresee, and it has to be a catch-all for that reason.
            _logger.LogError(ex, "Trimming the speech cache at {Directory} after a write failed; it may sit over budget until the next write or restart.", directory);
        }
        finally
        {
            ledger.EndTrim(snapshot, remaining);
        }
    }

    /// <summary>
    /// Trims the cache, oldest clip first, and returns the number of files removed. Called at startup over
    /// every household's drawer — the only sweep a household that never writes gets, and the one that
    /// clears orphans at the root. A household that DOES write is also swept after any write that takes
    /// it over budget (<see cref="Charge"/>), by this same per-drawer sweep, so there is one eviction
    /// policy however the trim was reached.
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

        // Listing the households can fail on its own (a locked or unreadable cache directory), and this
        // runs unguarded during startup — so a cache we can't even enumerate must stay a logged warning,
        // never an exception that stops the app booting. Trimming is a housekeeping nicety; it has no
        // business being able to take the deployment down.
        DirectoryInfo[] households;
        try
        {
            // Each immediate subfolder is one household (see HouseholdFolder).
            households = new DirectoryInfo(directory).GetDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't list the speech cache at {Directory}; skipping the trim.", directory);
            return 0;
        }

        // Clips loose at the ROOT are orphans by construction, so they go — all of them, whatever the
        // budget says. Every lookup builds its path through a household folder, so nothing can ever read
        // one again: not a cache hit, not "download my data", and not "delete my data", which is the one
        // that matters. They're audio of someone's recipes that no longer belongs to anyone we can name.
        // They exist because the cache filed everything flat before it filed per household; giving them a
        // budget would keep unreachable recordings on disk indefinitely, so their budget is nothing.
        var removed = TrimFolder(directory, maxBytes: 0, SearchOption.TopDirectoryOnly, logger)?.Removed ?? 0;
        foreach (var household in households)
        {
            removed += TrimFolder(household.FullName, maxBytesPerHousehold, SearchOption.AllDirectories, logger)?.Removed ?? 0;
        }
        return removed;
    }

    /// <summary>What one sweep did and what it left: <see cref="Removed"/> files deleted, and
    /// <see cref="Remaining"/> bytes of clips still in the folder once it finished — measured from the
    /// disk, which is what lets an after-write trim reset a household's running total to the truth
    /// rather than to an estimate of it.</summary>
    private readonly record struct FolderTrim(int Removed, long Remaining);

    /// <summary>The clips in a folder: the ONE enumeration, shared by the sweep and by the ledger's seed
    /// (<see cref="Measure"/>), so what a household is counted as holding is exactly what a trim would
    /// weigh. Temp files mid-write are <c>.tmp</c>, so neither ever counts a clip that isn't published.</summary>
    private static FileInfo[] ClipsIn(string directory, SearchOption depth) =>
        new DirectoryInfo(directory).GetFiles("*.audio", depth);

    private static long BytesOf(IEnumerable<FileInfo> clips) => clips.Sum(f => f.Length);

    /// <summary>Sweeps one folder down to <paramref name="maxBytes"/>, oldest clip first. Null when the
    /// folder could not be read at all — reported, not thrown, for the same reason as <see cref="Trim"/>.</summary>
    private static FolderTrim? TrimFolder(string directory, long maxBytes, SearchOption depth, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(directory)) return new FolderTrim(0, 0);

            var files = ClipsIn(directory, depth);
            var total = BytesOf(files);
            if (total <= maxBytes) return new FolderTrim(0, total);

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
            return new FolderTrim(removed, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // One household's unreadable folder must not stop the others being swept.
            logger.LogWarning(ex, "Couldn't trim the speech cache at {Directory}.", directory);
            return null;
        }
    }
}
