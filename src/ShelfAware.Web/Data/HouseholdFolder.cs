using System.Security.Cryptography;
using System.Text;

namespace ShelfAware.Web.Data;

/// <summary>
/// Where a household's files live on disk, and how to forget them. Shared by everything that files data
/// per household (the speech cache, receipt images), so there is one answer rather than one per feature.
/// </summary>
public static class HouseholdFolder
{
    /// <summary>A household's folder name: a HASH of its id, never the id itself. Ids are server-minted
    /// GUIDs today, but that's a property of code elsewhere — the day one becomes user-influenced, every
    /// path built from it would be a traversal. Hex can't traverse anything.</summary>
    public static string For(string householdId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(householdId)))[..32];

    /// <summary>Removes everything a household has filed under <paramref name="root"/>. Scoped by
    /// construction — it can only ever reach one household's folder.</summary>
    public static bool DeleteUnder(string root, string householdId, ILogger logger) =>
        DeleteTree(Path.Combine(root, For(householdId)), logger);

    /// <summary>
    /// Removes a directory tree if it's there, reporting rather than throwing when it won't go. Every
    /// caller is deleting files derived from data that is already gone, so failing here must not fail the
    /// operation — it would only invite the user to press the button again.
    ///
    /// It checks NOTHING about the path. Unlike <see cref="DeleteUnder"/> it isn't scoped to anything, so
    /// a caller must already have established that what it's handing over is theirs to remove — see
    /// <c>ReceiptStorage.DeleteFolder</c>, which proves containment first and would be a recursive delete
    /// of whatever a stored string said without it. Named for what it does rather than for the class it
    /// sits on, because that is the part worth noticing.
    /// </summary>
    public static bool DeleteTree(string folder, ILogger logger)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't remove {Folder}.", folder);
            return false;
        }
    }
}
