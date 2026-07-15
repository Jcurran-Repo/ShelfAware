namespace ShelfAware.Web.Data;

/// <summary>
/// Comparing filesystem paths, correctly for the platform we're actually on.
///
/// "Is this path inside that one" is a security question here — it confines the receipt folder, and it
/// stops the receipt store deleting outside itself — so the comparison has to match the filesystem it's
/// asking about. OrdinalIgnoreCase is right on Windows and WRONG on Linux, where <c>/inbox</c> and
/// <c>/INBOX</c> are two directories a case-insensitive check would call one.
/// </summary>
public static class PathScope
{
    /// <summary>Windows paths are case-insensitive; Linux paths are not. macOS is usually insensitive but
    /// can be formatted either way — it isn't a deploy target, and the safe assumption for an unknown
    /// platform is the strict one, since a false NON-match only ever refuses access.</summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whether <paramref name="candidate"/> IS <paramref name="root"/> or sits inside it. Both
    /// must already be fully resolved — this compares strings and cannot undo a "..".</summary>
    public static bool IsAtOrInside(string candidate, string root) =>
        string.Equals(candidate, root, Comparison) || IsInside(candidate, root);

    /// <summary>Whether <paramref name="candidate"/> sits STRICTLY inside <paramref name="root"/> — for
    /// callers where being handed the root would be a catastrophe rather than a no-op (deleting
    /// "receipts" is not the same as deleting one receipt's folder).</summary>
    public static bool IsInside(string candidate, string root)
    {
        // Compare against root PLUS a separator, or "/inbox-old" reads as inside "/inbox". A filesystem
        // root already ends in one, and appending a second would build a prefix no real path has —
        // rejecting the entire volume for anyone who set "C:\" as their allowed root.
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        // Longer than the prefix, so the root itself is "at" but never "inside".
        return candidate.Length > prefix.Length && candidate.StartsWith(prefix, Comparison);
    }
}
