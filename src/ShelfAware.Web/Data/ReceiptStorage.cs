namespace ShelfAware.Web.Data;

/// <summary>
/// Owns where receipt images live on disk, the way <c>CachingTextToSpeech</c> owns where clips live —
/// and for the same reason. The saved copy of a receipt is a photograph of a household's shopping, so
/// "delete my data" has to reach it, and a file you can't attribute is a file you can't delete. Hence a
/// per-household tree: a delete removes it wholesale rather than hoping every row was enumerated first.
///
/// <see cref="Core.Domain.Receipt.ImagePath"/> is stored RELATIVE to the data directory, so that
/// directory can move without rewriting the database, and with a FORWARD SLASH rather than the
/// platform's separator. The separator isn't cosmetic: a backslash is an ordinary filename character on
/// Linux, so a Windows-written path would read there as one long literal filename and every receipt's
/// copy would report as missing. Reads normalise either separator, so rows written before this rule
/// still resolve.
/// </summary>
public sealed class ReceiptStorage(AppPaths paths, ICurrentHousehold household, ILogger<ReceiptStorage> logger)
{
    private const string Root = "receipts";

    /// <summary>Creates a folder for a new receipt and returns its <c>ImagePath</c> — relative, household
    /// scoped, and unguessable (a timestamp for the human reading a directory listing, a GUID for
    /// everything else).</summary>
    public async Task<string> NewFolderAsync(CancellationToken cancellationToken = default)
    {
        var householdId = await household.GetRequiredIdAsync(cancellationToken);
        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..40];
        // Joined with '/', not Path.Combine: this string goes in the database, and it has to mean the same
        // thing on the machine that reads it as on the one that wrote it.
        var relative = $"{Root}/{HouseholdFolder.For(householdId)}/{name}";
        Directory.CreateDirectory(Absolute(relative));
        return relative;
    }

    /// <summary>Saves one page of a receipt. The index orders the pages; the media type picks the
    /// extension, so a later read knows what it's holding without sniffing.</summary>
    public async Task WritePageAsync(
        string imagePath, int index, byte[] bytes, string mediaType, CancellationToken cancellationToken = default)
    {
        var folder = Within(imagePath)
            ?? throw new InvalidOperationException($"Refusing to write a receipt page outside the receipts store: '{imagePath}'.");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"page-{index}.{ReceiptMediaTypes.ExtensionFor(mediaType)}");
        await File.WriteAllBytesAsync(file, bytes, cancellationToken);
    }

    /// <summary>The saved pages of a receipt, in page order — empty when the copy is missing (an older
    /// receipt, a hand-edited data directory, or a demo row that never had one).</summary>
    public IReadOnlyList<string> Pages(string imagePath)
    {
        var folder = Within(imagePath);
        if (folder is null || !Directory.Exists(folder)) return [];
        return [.. Directory.GetFiles(folder, "page-*.*").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
    }

    public bool HasPages(string imagePath) => Pages(imagePath).Count > 0;

    /// <summary>Reads a saved page as an attachment ready for the extractor.</summary>
    public async Task<(byte[] Bytes, string MediaType)> ReadPageAsync(
        string file, CancellationToken cancellationToken = default) =>
        (await File.ReadAllBytesAsync(file, cancellationToken), ReceiptMediaTypes.ForPath(file));

    /// <summary>Removes one receipt's saved copy. Used to reach rows filed before this type existed,
    /// whose <c>ImagePath</c> has no household segment and so isn't under the household's tree.</summary>
    public void DeleteFolder(string imagePath)
    {
        // Null means it doesn't resolve inside the store (the demo seeder's "demo/no-image" placeholder,
        // say): nothing of ours, and not something to go deleting on a stored string's say-so.
        if (Within(imagePath) is { } folder) HouseholdFolder.Delete(folder, logger);
    }

    /// <summary>Forgets every receipt image this household ever saved. Exposed as an operation for the
    /// same reason as the speech cache's equivalent: the caller shouldn't have to know how images are
    /// filed to be allowed to delete them.</summary>
    public async Task DeleteHouseholdAsync(CancellationToken cancellationToken = default)
    {
        if (await household.GetIdAsync(cancellationToken) is { } householdId)
        {
            HouseholdFolder.DeleteUnder(Absolute(Root), householdId, logger);
        }
    }

    private string Absolute(string relative) => Path.Combine(paths.DataDir, ForThisPlatform(relative));

    /// <summary>A stored <c>ImagePath</c> with whatever separator wrote it, turned into one this platform
    /// understands. Both directions are handled, so a database written on Windows still finds its images
    /// on Linux and vice versa. Safe for these paths specifically: every segment we generate is "receipts",
    /// a hex hash, or a timestamp-and-GUID, none of which can contain either separator — so there is no
    /// legitimate backslash here to mistake for a directory break.</summary>
    private static string ForThisPlatform(string relative) =>
        relative.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Resolves a stored <c>ImagePath</c> and proves it lands inside the receipts store, or
    /// returns null. These strings come from our own DB rather than a request, so this is belt-and-braces
    /// — but a delete that trusts a stored path is one bad row away from removing something else, and the
    /// check costs nothing.</summary>
    private string? Within(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(paths.DataDir, Root)));
        string full;
        try
        {
            full = Path.GetFullPath(Absolute(imagePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            logger.LogWarning(ex, "Unusable receipt image path {ImagePath}.", imagePath);
            return null;
        }

        // STRICTLY inside: being handed the store root itself must not resolve to "delete every
        // household's receipts". PathScope also gets the platform right — a case-insensitive compare
        // would call /receipts and /RECEIPTS the same directory on the Linux deploy target.
        return PathScope.IsInside(full, root) ? full : null;
    }
}
