namespace ShelfAware.Core.Speech;

/// <summary>
/// The stored audio of a household's recipes, addressed the way its owner thinks about it — by what was
/// said — rather than by where it happens to sit on a disk.
///
/// Two callers need this and neither should have to know how clips are filed: "delete my data" has to
/// reach the audio or it isn't true, and "download my data" has to reach it or it isn't everything.
/// Registered only when caching is switched on, so it's a nullable dependency: no cache, nothing to find
/// or forget.
/// </summary>
public interface ISpeechCache
{
    /// <summary>The stored clip for exactly this text in exactly this position, or null if it was never
    /// synthesized (or has since been trimmed). The context matters: neighbouring segments are part of
    /// what a clip sounds like, so they're part of what identifies it.</summary>
    Task<StoredClip?> FindAsync(
        string householdId, string text, SpeechContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>Forget everything ever spoken for one household. False if something was there and
    /// wouldn't go.</summary>
    bool DeleteHousehold(string householdId);
}

/// <summary>Audio already on disk: the bytes, and the media type needed to name a file for them.</summary>
public sealed record StoredClip(byte[] Audio, string MediaType);
