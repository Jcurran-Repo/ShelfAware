using System.Security.Cryptography;
using System.Text;

namespace ShelfAware.Web.Data;

/// <summary>
/// The on-disk folder name for a household: a HASH of its id, never the id itself.
///
/// A household id has no business being concatenated into a filesystem path. Today they're
/// server-minted GUIDs and safe, but that's a property of code elsewhere, and the day one becomes
/// user-influenced every such path would be a traversal. Hex can't traverse anything. Deterministic,
/// so a delete can always find what it needs to remove.
///
/// Shared by everything that files data per household on disk (the speech cache, receipt images), so
/// there is one answer to "where does this household's stuff live" rather than one per feature.
/// </summary>
public static class HouseholdFolder
{
    public static string For(string householdId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(householdId)))[..32];
}
