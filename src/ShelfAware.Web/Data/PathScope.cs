namespace ShelfAware.Web.Data;

/// <summary>
/// Comparing filesystem paths, correctly for the platform we're actually on.
///
/// This exists because "is this path inside that one" is a security question in two places now — the
/// receipt-folder policy confining a path a user typed, and the receipt store refusing to delete outside
/// itself — and both got it subtly wrong in the same way: an OrdinalIgnoreCase comparison. That's right
/// on Windows (the self-host) and WRONG on Linux (the Azure deploy target), where <c>/inbox</c> and
/// <c>/INBOX</c> are two different directories that a case-insensitive check would call the same one.
/// Neither was exploitable — escaping needed a case-variant directory holding something worth reading,
/// which nothing lets an attacker create — but a containment check that can call two different
/// directories equal is not a containment check, and it was one shared bug in two copies.
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

    /// <summary>Whether <paramref name="candidate"/> sits STRICTLY inside <paramref name="root"/> — the
    /// root itself doesn't count. For callers where being handed the root would be a catastrophe rather
    /// than a no-op (deleting "receipts" is not the same as deleting one receipt's folder).
    ///
    /// The comparison is against the root PLUS a separator, which is the whole point: without it
    /// "<c>/inbox-old</c>" starts with "<c>/inbox</c>" as a string while being nobody's idea of inside it.
    ///
    /// A filesystem root already ends in that separator, though, and appending a second one produces a
    /// prefix no real path has — so "<c>C:\</c>" or "<c>/</c>" as the root would reject everything on the
    /// volume. That fails closed rather than open, but "allow anything on this drive" is exactly what
    /// someone types when they want the loosest confinement that still counts as confinement, and it
    /// should work.</summary>
    public static bool IsInside(string candidate, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        // Longer than the prefix, so the root itself is "at" but never "inside".
        return candidate.Length > prefix.Length && candidate.StartsWith(prefix, Comparison);
    }
}
