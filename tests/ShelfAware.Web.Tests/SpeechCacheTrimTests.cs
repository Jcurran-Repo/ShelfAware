using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The trim budget is per household. One shared budget over the whole tree was the last cross-tenant
/// thing about the speech cache after the clips themselves were separated: it deleted the oldest clips
/// anywhere, so a household that cooks a lot evicted the recipes of one that doesn't — who then paid to
/// re-synthesize audio they had already bought.
/// </summary>
public class SpeechCacheTrimTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "shelfaware-tts-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>One clip of <paramref name="bytes"/> bytes, written at <paramref name="ageDays"/> old so
    /// the oldest-first order is deterministic rather than dependent on how fast the test runs.</summary>
    private string Clip(string household, string name, int bytes, int ageDays)
    {
        var folder = Path.Combine(_root, household);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".audio");
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    [Fact]
    public void A_heavy_household_does_not_evict_a_light_ones_clips()
    {
        // B is over its own budget. A is well under it — but A's clips are the oldest in the tree, so a
        // single shared budget would have deleted A's to make room for B's.
        var lightAndOld = Clip("household-a", "old-but-mine", 100, ageDays: 30);
        Clip("household-b", "big-1", 900, ageDays: 2);
        Clip("household-b", "big-2", 900, ageDays: 1);

        CachingTextToSpeech.Trim(_root, maxBytesPerHousehold: 1000, NullLogger.Instance);

        Assert.True(File.Exists(lightAndOld), "A's clip was evicted by B's usage.");
    }

    [Fact]
    public void A_household_over_its_budget_loses_its_own_oldest_first()
    {
        var oldest = Clip("household-b", "oldest", 900, ageDays: 10);
        var newest = Clip("household-b", "newest", 900, ageDays: 1);

        var removed = CachingTextToSpeech.Trim(_root, maxBytesPerHousehold: 1000, NullLogger.Instance);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void A_household_within_its_budget_is_left_alone()
    {
        var kept = Clip("household-a", "small", 100, ageDays: 99);

        Assert.Equal(0, CachingTextToSpeech.Trim(_root, maxBytesPerHousehold: 1000, NullLogger.Instance));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void Clips_left_loose_at_the_root_are_still_swept()
    {
        // Written before the cache was split by household: nothing can attribute them, so nothing else
        // would ever remove them.
        Directory.CreateDirectory(_root);
        var loose = Path.Combine(_root, "orphan.audio");
        File.WriteAllBytes(loose, new byte[2000]);

        CachingTextToSpeech.Trim(_root, maxBytesPerHousehold: 1000, NullLogger.Instance);

        Assert.False(File.Exists(loose));
    }

    [Fact]
    public void A_missing_cache_directory_is_not_an_error()
    {
        Assert.Equal(0, CachingTextToSpeech.Trim(_root, maxBytesPerHousehold: 1000, NullLogger.Instance));
    }
}
