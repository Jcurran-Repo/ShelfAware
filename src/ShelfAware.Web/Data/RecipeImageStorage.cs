namespace ShelfAware.Web.Data;

/// <summary>
/// Owns where recipe images live on disk — the recipe-photo analogue of <see cref="ReceiptStorage"/>,
/// and for the same reasons: a photo of a household's cooking is theirs, so "delete my data" must reach
/// it, hence a per-household tree a delete can remove wholesale. One image per recipe.
///
/// <see cref="Core.Domain.Recipe.ImagePath"/> is stored RELATIVE to the data directory (so it can move
/// without rewriting the DB) and with a FORWARD SLASH (a backslash is an ordinary filename character on
/// Linux; reads normalise either separator). Unlike receipts (a folder of pages), a recipe has a single
/// file whose GUID name changes on every replace — which doubles as the browser cache-buster, so a
/// replaced photo lands at a new URL rather than serving a stale cached copy.
/// </summary>
public sealed class RecipeImageStorage(AppPaths paths, ICurrentHousehold household, ILogger<RecipeImageStorage> logger)
{
    private const string Root = "recipe-images";

    /// <summary>Writes the image bytes for a recipe and returns its new <c>ImagePath</c> — relative,
    /// household-scoped, and unguessable (a fresh GUID). The caller stores the returned path on the recipe.</summary>
    public async Task<string> SaveAsync(byte[] bytes, string mediaType, CancellationToken cancellationToken = default)
    {
        var householdId = await household.GetRequiredIdAsync(cancellationToken);
        // Joined with '/', not Path.Combine: this string goes in the database and must mean the same
        // thing on the machine that reads it as on the one that wrote it.
        var relative = $"{Root}/{HouseholdFolder.For(householdId)}/{Guid.NewGuid():N}.{ReceiptMediaTypes.ExtensionFor(mediaType)}";
        var absolute = Within(relative)
            ?? throw new InvalidOperationException($"Refusing to write a recipe image outside the store: '{relative}'.");
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllBytesAsync(absolute, bytes, cancellationToken);
        return relative;
    }

    /// <summary>Reads a stored recipe image (bytes + media type), or null when the file is missing,
    /// unreadable, or — belt-and-braces on a string from our own DB — doesn't resolve inside the store.
    /// Fully fail-soft (an unreadable file returns null, never throws) so it's safe both for the serving
    /// endpoint and mid-export, where the response has already started and an escaping IO error would
    /// truncate the download.</summary>
    public async Task<(byte[] Bytes, string MediaType)?> ReadAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        var full = Within(imagePath);
        if (full is null || !File.Exists(full)) return null;
        try
        {
            return (await File.ReadAllBytesAsync(full, cancellationToken), ReceiptMediaTypes.ForPath(full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't read recipe image {ImagePath}.", imagePath);
            return null;
        }
    }

    /// <summary>Removes one recipe's saved image (household-guarded). No-op if it doesn't resolve inside
    /// the store or is already gone.</summary>
    public void Delete(string imagePath)
    {
        if (Within(imagePath) is not { } full || !File.Exists(full)) return;
        try
        {
            File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't delete recipe image {ImagePath}.", imagePath);
        }
    }

    /// <summary>Forgets every recipe image this household saved — the "delete my data" reach, mirroring
    /// <see cref="ReceiptStorage.DeleteHouseholdAsync"/>.</summary>
    public async Task DeleteHouseholdAsync(CancellationToken cancellationToken = default)
    {
        if (await household.GetIdAsync(cancellationToken) is { } householdId)
        {
            HouseholdFolder.DeleteUnder(Absolute(Root), householdId, logger);
        }
    }

    private string Absolute(string relative) => Path.Combine(paths.DataDir, ForThisPlatform(relative));

    // A stored path with whatever separator wrote it, turned into one this platform understands. Safe for
    // these paths: every segment we generate is "recipe-images", a hex hash, or a GUID — none can contain
    // either separator, so there's no legitimate backslash to mistake for a directory break.
    private static string ForThisPlatform(string relative) =>
        relative.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Resolves a stored <c>ImagePath</c> and proves it lands STRICTLY inside the recipe-image
    /// store, or returns null — the one guard between a stored string and a filesystem read/delete. (Cross-
    /// household protection is the DB query filter that reads the path in the first place; this is
    /// belt-and-braces against a malformed row, exactly as in ReceiptStorage.)</summary>
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
            logger.LogWarning(ex, "Unusable recipe image path {ImagePath}.", imagePath);
            return null;
        }

        // STRICTLY inside: being handed the store root itself must not resolve to "every household's
        // images". PathScope gets the platform right (case-sensitive on the Linux deploy target).
        return PathScope.IsInside(full, root) ? full : null;
    }
}
