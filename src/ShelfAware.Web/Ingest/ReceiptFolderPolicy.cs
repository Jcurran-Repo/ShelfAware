using Microsoft.Extensions.Options;

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
    private readonly string? _allowedRoot = Normalize(options.Value.AllowedRoot);

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

    /// <summary>The gate the inbox uses: an allowed folder comes back, anything else comes back null and
    /// is read as "no folder configured".</summary>
    public string? Permit(string? folder) =>
        string.IsNullOrWhiteSpace(folder) || Reject(folder) is not null ? null : folder.Trim();

    private bool IsWithin(string full)
    {
        // The separator matters: without it, "C:\inbox-old" reads as being inside "C:\inbox".
        if (string.Equals(full, _allowedRoot, StringComparison.OrdinalIgnoreCase)) return true;
        return full.StartsWith(_allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GetFullPath is what makes the check meaningful: it resolves "..", "." and relative
    /// segments, so a path can't walk out of the root and back in on a technicality.</summary>
    private static string? Resolve(string folder)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? Normalize(string? root) =>
        string.IsNullOrWhiteSpace(root) ? null : Resolve(root);
}
