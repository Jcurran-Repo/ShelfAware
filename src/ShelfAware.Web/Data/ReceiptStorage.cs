using System.IO.Compression;

namespace ShelfAware.Web.Data;

/// <summary>A receipt's saved copy packaged for a browser download: the bytes, the content type to serve
/// them as, and the filename to save under. One saved page comes back as its own image/PDF; several as a
/// zip of the pages.</summary>
public readonly record struct ReceiptDownload(byte[] Bytes, string MediaType, string FileName);

/// <summary>The one thing the receipt-confirm UNDO needs from receipt storage: forget a receipt's saved
/// image after its rows are gone. A narrow seam over <see cref="ReceiptStorage"/> so the undo handler stays
/// cheap to construct (its only dependency) like every other handler, and so a test can prove the undo asks
/// to delete the RIGHT folder — and that a Peek never does — without a filesystem.</summary>
public interface IReceiptImageCleanup
{
    void DeleteFolder(string imagePath);
}

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
    : IReceiptImageCleanup
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
        // Order by the numeric page index, not the filename string: an ordinal sort puts page-10 before
        // page-2, so a 10+ page receipt (the upload cap is 20) would misorder the extractor's re-read and
        // mislabel the download zip's renumbered entries.
        return [.. Directory.GetFiles(folder, "page-*.*").OrderBy(PageIndex)];
    }

    // The <n> in "page-<n>.<ext>". A malformed name sorts last rather than throwing.
    private static int PageIndex(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var dash = name.LastIndexOf('-');
        return dash >= 0 && int.TryParse(name.AsSpan(dash + 1), out var n) ? n : int.MaxValue;
    }

    public bool HasPages(string imagePath) => Pages(imagePath).Count > 0;

    /// <summary>Reads a saved page as an attachment ready for the extractor.</summary>
    public async Task<(byte[] Bytes, string MediaType)> ReadPageAsync(
        string file, CancellationToken cancellationToken = default) =>
        (await File.ReadAllBytesAsync(file, cancellationToken), ReceiptMediaTypes.ForPath(file));

    /// <summary>Packages a receipt's saved copy for a browser download: the single page as-is when there
    /// is exactly one, or a zip of the pages in order when there are several. Null when the copy is
    /// missing (an older receipt, a demo row, a hand-edited data dir). <paramref name="baseName"/> is the
    /// download filename WITHOUT extension — this adds the page's own extension, or ".zip".</summary>
    public async Task<ReceiptDownload?> ReadForDownloadAsync(
        string imagePath, string baseName, CancellationToken cancellationToken = default)
    {
        var pages = Pages(imagePath);
        if (pages.Count == 0) return null;

        if (pages.Count == 1)
        {
            var (bytes, mediaType) = await ReadPageAsync(pages[0], cancellationToken);
            return new ReceiptDownload(bytes, mediaType, $"{baseName}.{Extension(pages[0])}");
        }

        // Build the zip in memory, not on the response stream: ZipArchive's writes are synchronous, and a
        // MemoryStream doesn't care — so this needs no AllowSynchronousIO opt-in (unlike the data export,
        // which zips straight onto Kestrel's response body). Receipts are a handful of pages, so the whole
        // archive comfortably fits in memory.
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var (bytes, _) = await ReadPageAsync(pages[i], cancellationToken);
                var entry = zip.CreateEntry($"page-{i + 1}.{Extension(pages[i])}", CompressionLevel.Fastest);
                await using var entryStream = entry.Open();
                entryStream.Write(bytes, 0, bytes.Length);
            }
        }
        return new ReceiptDownload(buffer.ToArray(), "application/zip", $"{baseName}.zip");
    }

    private static string Extension(string file) => Path.GetExtension(file).TrimStart('.');

    /// <summary>Removes one receipt's saved copy. Used to reach rows filed before this type existed,
    /// whose <c>ImagePath</c> has no household segment and so isn't under the household's tree.</summary>
    public void DeleteFolder(string imagePath)
    {
        // Within is what makes the delete below safe: null means the path doesn't resolve inside the
        // store (the demo seeder's "demo/no-image" placeholder, say), and DeleteTree checks nothing
        // itself — so this guard is the only thing between a stored string and a recursive delete.
        if (Within(imagePath) is { } folder) HouseholdFolder.DeleteTree(folder, logger);
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
