using System.Collections.Concurrent;

namespace ShelfAware.Web.Services;

/// <summary>
/// What each household's speech cache is believed to hold on disk, and the one-trim-at-a-time gate that
/// keeps a drawer from being swept twice. Singleton, because <see cref="CachingTextToSpeech"/> is
/// transient — a running total kept on the cache would be a running total per request.
///
/// <para>This exists so the cache can keep a household under its budget WITHOUT scanning the drawer on
/// every write. The in-process voices write WAV at ~48 KB per spoken second for free, so a household
/// reading its way through a cookbook on a box that is up for weeks ran far past
/// <c>Speech:CacheMegabytes</c> between restarts — the cap was only enforced at boot. Now each write is an
/// integer add and a compare against the ledger here; the disk is only scanned the first time a household
/// writes after boot (to seed its total), and when a write takes it over budget (the trim itself, which
/// re-measures and corrects the total). Everything in between is arithmetic.</para>
///
/// <para>The totals are an ESTIMATE that errs high, never low: a clip re-published over an identical one
/// (two circuits racing to voice the same step) is counted twice, and a write that lands while a trim is
/// mid-scan may be counted by both. An overestimate only makes a trim run early, and every trim re-reads
/// the disk and resets the total to what it found — so the estimate can drift up between trims but is
/// corrected by each one, and can never let a household sit over budget unnoticed.</para>
/// </summary>
public sealed class SpeechCacheBudget
{
    private readonly ConcurrentDictionary<string, HouseholdLedger> _ledgers = new(StringComparer.Ordinal);

    public SpeechCacheBudget(long maxBytesPerHousehold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerHousehold);
        MaxBytesPerHousehold = maxBytesPerHousehold;
    }

    /// <summary>The budget each household's drawer is kept under — the same number the startup
    /// <see cref="CachingTextToSpeech.Trim"/> sweeps against, so "over budget" means one thing.</summary>
    public long MaxBytesPerHousehold { get; }

    /// <summary>What the ledger believes <paramref name="householdId"/> holds, in bytes; zero for a
    /// household that has not written since boot, whose drawer has never been measured and so is left
    /// exactly as the startup trim left it.</summary>
    public long BytesHeldBy(string householdId) =>
        _ledgers.TryGetValue(householdId, out var ledger) ? ledger.Bytes : 0;

    /// <summary>Drops a household's running total. Called when its drawer is deleted: a total that outlived
    /// the files it counted would make the household's next write look over budget and trigger a scan of
    /// an empty folder. The next write re-seeds from the disk, as a first write after boot does.</summary>
    public void Forget(string householdId) => _ledgers.TryRemove(householdId, out _);

    /// <summary>Completes once no after-write trim is in flight for any household. The trims are detached
    /// from the request that caused them, so this is how a caller that needs them finished — a test
    /// asserting on the disk, an orderly shutdown — waits for the housekeeping.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_ledgers.Values.Select(ledger => ledger.Trim));

    internal HouseholdLedger For(string householdId) =>
        _ledgers.GetOrAdd(householdId, static _ => new HouseholdLedger());

    /// <summary>One household's running total and trim gate. Every member is lock-free: a write's charge is
    /// one interlocked add, which is the whole point of keeping a ledger instead of scanning.</summary>
    internal sealed class HouseholdLedger
    {
        private long _bytes;
        private int _seeded;
        private int _trimming;
        private Task _trim = Task.CompletedTask;

        public long Bytes => Volatile.Read(ref _bytes);

        /// <summary>The most recent after-write trim, finished or not.</summary>
        public Task Trim => Volatile.Read(ref _trim);

        /// <summary>True for exactly one caller, ever: the write that must MEASURE the drawer rather than
        /// add to a total that does not exist yet. Everyone else — including a write racing the measurer —
        /// adds its own bytes, which can double-count a clip the scan also saw. That errs high, which is the
        /// safe direction (see <see cref="SpeechCacheBudget"/>).</summary>
        public bool TryClaimSeed() => Interlocked.CompareExchange(ref _seeded, 1, 0) == 0;

        /// <summary>Adds bytes just written (or just measured) and returns the new total.</summary>
        public long Add(long bytes) => Interlocked.Add(ref _bytes, bytes);

        /// <summary>Claims the household's one trim slot, handing back the total as it stood at the claim
        /// so the trim can reconcile against it. False when a trim is already in flight: that trim will
        /// weigh whatever is on disk when it looks, and anything that lands after it looked is the next
        /// crossing write's to trigger.</summary>
        public bool TryBeginTrim(out long snapshot)
        {
            if (Interlocked.CompareExchange(ref _trimming, 1, 0) != 0)
            {
                snapshot = 0;
                return false;
            }
            snapshot = Bytes;
            return true;
        }

        /// <summary>Records the detached task a claimed trim runs on, for <see cref="SpeechCacheBudget.WhenIdleAsync"/>.</summary>
        public void Track(Task trim) => Volatile.Write(ref _trim, trim);

        /// <summary>Releases the slot. <paramref name="remaining"/> is what the trim measured on disk once
        /// it had finished deleting, or null when it could not measure at all. The total becomes that
        /// measurement plus whatever was charged since <paramref name="snapshot"/> — one add, because
        /// <c>(now − snapshot) + remaining</c> is <c>now + (remaining − snapshot)</c>. A trim that could not
        /// measure leaves the total alone: the next crossing write tries again.</summary>
        public void EndTrim(long snapshot, long? remaining)
        {
            if (remaining is { } measured) Interlocked.Add(ref _bytes, measured - snapshot);
            Volatile.Write(ref _trimming, 0);
        }
    }
}
