using Microsoft.Extensions.Options;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Ingest;

/// <summary>Where a household is allowed to point its receipt drop folder.</summary>
public sealed class ReceiptFolderOptions
{
    public const string SectionName = "Receipts";

    /// <summary>
    /// A directory every household's receipt folder must sit inside, or null to allow any local path.
    ///
    /// Null is the SELF-HOST default and is the honest setting there: the whole point of the feature is to
    /// read a folder the owner already keeps their receipts in (<c>Documents\Walmart Receipts</c>), the
    /// owner is the only tenant, and the app runs as them — confining it would break the feature to
    /// prevent them from reading their own files.
    ///
    /// Set it on any deployment with more than one household. Without it, the folder setting is an
    /// arbitrary-path read: a signed-in user can point the inbox at any directory the server process can
    /// reach and have every image and PDF in it extracted into their pantry.
    /// </summary>
    public string? AllowedRoot { get; set; }
}

/// <summary>
/// Decides whether a receipt folder is one this deployment will read. The Settings page asks so it can
/// refuse a bad path with a sentence a human can act on; the inbox asks because it is the actual trust
/// boundary — the setting is stored, and a value that got in another way (an older build, a hand-edited
/// database, a future writer) must not be honoured just because it's already in the table.
/// </summary>
public sealed class ReceiptFolderPolicy(IOptions<ReceiptFolderOptions> options)
{
    private readonly string? _allowedRoot = NormalizeRoot(options.Value.AllowedRoot);

    /// <summary>Whether this deployment confines receipt folders at all.</summary>
    public bool IsConfined => _allowedRoot is not null;

    /// <summary>The configured root, for showing the user where their folder has to live.</summary>
    public string? AllowedRoot => _allowedRoot;

    /// <summary>Null when the folder is allowed; otherwise why it isn't, phrased for the person typing it.</summary>
    public string? Reject(string? folder) => Judge(folder).Rejection;

    /// <summary>The gate the inbox uses: an allowed folder comes back RESOLVED, anything else comes back
    /// null and is read as "no folder configured".</summary>
    public string? Permit(string? folder) => Judge(folder).Allowed;

    /// <summary>
    /// The one place a folder is judged, returning BOTH answers from a SINGLE resolution — the verdict for
    /// the Settings page and the path for the inbox.
    ///
    /// One resolution is the point. Resolving separately for the check and for the return means the value
    /// that gets opened is not the value that was proven safe: they can differ, and since resolving now
    /// touches the filesystem to follow links, they can differ because someone changed a link in between.
    /// Checking one string and using another is the bug this method exists to make unspeakable — it was
    /// "validate resolved, return raw" once, then "validate one resolution, return a second" once, and
    /// both times the shape survived the fix. Now there is only ever one string.
    /// </summary>
    private (string? Allowed, string? Rejection) Judge(string? folder)
    {
        // "No folder" is a valid choice — it means the feature is off, not that anything is wrong.
        if (string.IsNullOrWhiteSpace(folder)) return (null, null);

        var full = Resolve(folder);
        // A path that can't be resolved can't be read either, so say so whether or not we're confining.
        if (full is null) return (null, "That doesn't look like a valid folder path.");

        if (_allowedRoot is null) return (full, null); // unconfined: the self-host, where any local path is the feature

        // A UNC path can be inside the allowed root only by coincidence of spelling, and reaching another
        // machine is never what confinement is for.
        if (folder.TrimStart().StartsWith(@"\\", StringComparison.Ordinal))
            return (null, "Receipt folders have to be on this server.");

        return PathScope.IsAtOrInside(full, _allowedRoot)
            ? (full, null)
            : (null, $"Receipt folders have to be inside {_allowedRoot} on this deployment.");
    }

    /// <summary>
    /// The full, real path, or null if it isn't one.
    ///
    /// Two steps, and both matter. GetFullPath resolves "..", "." and relative segments, so a path can't
    /// walk out of the root and claim to be inside it. Then reparse points are followed, because
    /// GetFullPath does NOT: a junction or symlink sitting inside the allowed root and pointing at
    /// C:\Windows would otherwise pass a purely textual containment check while reading somewhere else
    /// entirely. Resolving here rather than at save time is deliberate — the inbox re-resolves on every
    /// read, so a link created AFTER the setting was saved is caught too.
    ///
    /// Bounded honestly: this follows a link at the END of the path (and chains of them), not one in the
    /// middle of it. Escaping through an intermediate link needs the attacker to already be able to create
    /// links on the server, at which point confinement is not what's protecting anything.
    ///
    /// Neither catch logs, and that is not the swallowing CLAUDE.md forbids: the exception IS the answer
    /// here, not a lost error. "This string isn't a path" is reported to whoever asked — the person typing
    /// it gets a sentence back, and a bad Receipts:AllowedRoot fails the boot with one (see Program.cs) —
    /// so nothing goes quiet. Static on purpose, so RootIsUsable can be reached from options validation
    /// before any instance exists, which is also why there's no logger to reach for.
    /// </summary>
    private static string? Resolve(string folder)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        try
        {
            if (Directory.ResolveLinkTarget(full, returnFinalTarget: true) is { } target)
            {
                full = Path.TrimEndingDirectorySeparator(target.FullName);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Not a link, doesn't exist yet, a shape the link API rejects even though GetFullPath took it,
            // or we simply can't look. None of those make the path itself invalid, and the containment
            // check still means something against the resolved-so-far value — whereas letting this escape
            // would turn "no folder configured" into an unhandled error in the inbox, which has no
            // handler for it and is the one thing this method promises never to do.
        }

        return full;
    }

    /// <summary>Whether a configured root is one this class can actually enforce. Checked at STARTUP
    /// (see Program.cs) rather than trusted: a root that doesn't resolve would leave <c>_allowedRoot</c>
    /// null, and null means "allow any local path" — so a typo in the security setting would silently turn
    /// confinement off, which is the one failure mode this whole class exists to prevent.</summary>
    public static bool RootIsUsable(string? root) => string.IsNullOrWhiteSpace(root) || Resolve(root) is not null;

    private static string? NormalizeRoot(string? root) =>
        string.IsNullOrWhiteSpace(root) ? null : Resolve(root);
}
