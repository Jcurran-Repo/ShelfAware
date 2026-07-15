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
    public string? Reject(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null; // "no folder" is a valid choice — the feature is off
        if (_allowedRoot is null) return null;

        var full = Resolve(folder);
        if (full is null) return "That doesn't look like a valid folder path.";

        // A UNC path can be inside the allowed root only by coincidence of spelling, and reaching another
        // machine is never what confinement is for.
        if (folder.TrimStart().StartsWith(@"\\", StringComparison.Ordinal))
            return "Receipt folders have to be on this server.";

        return IsWithin(full)
            ? null
            : $"Receipt folders have to be inside {_allowedRoot} on this deployment.";
    }

    /// <summary>The gate the inbox uses: an allowed folder comes back RESOLVED, anything else comes back
    /// null and is read as "no folder configured".
    ///
    /// It returns the resolved path, not the string it was handed, so the path that gets opened is the
    /// exact one that was checked. Handing back the raw input worked only because .NET happened to resolve
    /// it identically at open time — validate-one-string/use-another is a bypass waiting for the two
    /// normalisations to diverge.</summary>
    public string? Permit(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        return Reject(folder) is null ? Resolve(folder) : null;
    }

    private bool IsWithin(string full) => PathScope.IsAtOrInside(full, _allowedRoot!);

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not a link, doesn't exist yet, or we can't look: none of those make the path itself invalid,
            // and the containment check below is still meaningful on the resolved-so-far value.
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
