using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Speech;

namespace ShelfAware.Web.Data;

/// <summary>
/// The one place that reads out or wipes everything the user has accumulated — backing both the
/// "Download my data" export and the "Delete my data" danger action. Kept as a service (not inline in
/// the page) so the delete order lives in exactly one tested spot and the export endpoint can reuse it.
///
/// Scope: the user's own content (pantry, purchase history, signals, receipts, recipes, lists) — plus the
/// two things DERIVED from that content and stored outside the rows, which are therefore just as much
/// theirs: the saved images of their receipts, and the synthesized audio of their recipe steps. It does
/// NOT touch the visitor's BYOK keys — those are held in the browser and cleared by "Forget my key".
///
/// **The export is everything in the household's database, deliberately** — including its settings and
/// its AI usage history, which the delete treats differently (settings split into config-that-stays and
/// content-that-goes; usage stays). Export and delete answer different questions. Delete asks "what is
/// yours to remove", and the answers are allowed to differ: config surviving a wipe is a kindness, and
/// usage surviving is what stops a delete doubling as a quota reset. Export asks "what do you have on
/// me", and the only defensible answer to that is all of it.
/// </summary>
/// <param name="speechCache">The household's stored recipe audio, or null when caching is off. Both
/// halves need it: deleting a household's rows while leaving a recording of its recipes on disk would
/// make "delete my data" a false statement, and an export without it isn't everything.</param>
public sealed class UserDataService(
    IHouseholdDbFactory dbFactory,
    ICurrentHousehold currentHousehold,
    ReceiptStorage receiptStorage,
    ISpeechCache? speechCache,
    ILogger<UserDataService> logger)
{
    /// <summary>Everything, flattened (loaded without navigations so there are no serialization cycles) —
    /// a portable JSON snapshot the user can keep before deleting, or just as a backup. Every table in the
    /// household's database is here; if a new one is added, it belongs here too.</summary>
    public async Task<DataExport> ExportAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return new DataExport
        {
            ExportedAt = DateTimeOffset.Now,
            Settings = await db.AppSettings.AsNoTracking().ToListAsync(ct),
            AiUsage = await db.AiUsages.AsNoTracking().ToListAsync(ct),
            Products = await db.Products.AsNoTracking().ToListAsync(ct),
            Purchases = await db.PurchaseEvents.AsNoTracking().ToListAsync(ct),
            Signals = await db.InventorySignals.AsNoTracking().ToListAsync(ct),
            Tags = await db.ProductTags.AsNoTracking().ToListAsync(ct),
            Substitutes = await db.ProductSubstitutes.AsNoTracking().ToListAsync(ct),
            Aliases = await db.ProductAliases.AsNoTracking().ToListAsync(ct),
            Receipts = await db.Receipts.AsNoTracking().ToListAsync(ct),
            ReceiptLines = await db.ReceiptLines.AsNoTracking().ToListAsync(ct),
            Recipes = await db.Recipes.AsNoTracking().ToListAsync(ct),
            RecipeIngredients = await db.RecipeIngredients.AsNoTracking().ToListAsync(ct),
            RecipeSteps = await db.RecipeSteps.AsNoTracking().ToListAsync(ct),
            ExcludedFoods = await db.ExcludedFoods.AsNoTracking().ToListAsync(ct),
            GroceryExtras = await db.GroceryExtras.AsNoTracking().ToListAsync(ct),
        };
    }

    /// <summary>
    /// The whole export as a ZIP, written straight to <paramref name="destination"/>.
    ///
    /// A zip because the export stopped being only text: the pantry's rows go in as <c>data.json</c>, the
    /// saved receipt images go in beside them, and so does the synthesized audio of the recipes. It writes
    /// as it goes rather than building the archive in memory — a household's receipt photos can run to
    /// tens of megabytes and there's no reason for the server to hold them all at once. (Images stream
    /// page by page; a clip is read whole, which is bounded by the largest single clip and not worth a
    /// stream-returning cache API to avoid.)
    ///
    /// The layout is meant to be opened by a person, not just parsed: <c>receipts/</c> mirrors each row's
    /// <c>ImagePath</c>, so a receipt in data.json names the folder its photo is in; and
    /// <c>recipes/&lt;name&gt;/step-3.mp3</c> is the clip of that recipe's third step, because a
    /// content-addressed hash is honest but useless to the person who asked for their data.
    /// </summary>
    public async Task WriteArchiveAsync(Stream destination, CancellationToken ct = default)
    {
        var snapshot = await ExportAsync(ct);
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var json = zip.CreateEntry("data.json", CompressionLevel.Optimal);
        await using (var entry = json.Open())
        {
            await JsonSerializer.SerializeAsync(entry, snapshot, JsonOptions, ct);
        }

        await AddReceiptImagesAsync(zip, snapshot.Receipts, ct);
        await AddRecipeAudioAsync(zip, snapshot.Recipes, snapshot.RecipeSteps, ct);
    }

    /// <summary>Every saved receipt page, filed under the same relative path the row already carries — so
    /// a Receipt in data.json points at its own photo in the archive. Already-compressed images get
    /// CompressionLevel.NoCompression: zipping a JPEG twice costs CPU and saves nothing.</summary>
    private async Task AddReceiptImagesAsync(ZipArchive zip, IReadOnlyList<Receipt> receipts, CancellationToken ct)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var receipt in receipts)
        {
            foreach (var page in receiptStorage.Pages(receipt.ImagePath))
            {
                ct.ThrowIfCancellationRequested();
                var name = $"{ZipFolderFor(receipt.ImagePath)}/{Path.GetFileName(page)}";
                // Two rows can't be made to share an ImagePath today (each mints its own GUID), but a zip
                // permits duplicate names and extraction tools disagree about what to do with them.
                if (!used.Add(name)) continue;
                await AddFileAsync(zip, name, page, ct);
            }
        }
    }

    /// <summary>
    /// One file into the archive, or a logged miss.
    ///
    /// A page that won't open must not take the download with it. By the time this runs the response has
    /// started, so an escaping exception can't become an error page — Kestrel just drops the connection
    /// and the user is left with a truncated zip and no idea why. One locked file (the importer mid-write,
    /// a virus scanner holding a handle, a page deleted since it was listed) costs them that page, not
    /// their whole export. Everything else on this path already fails soft for the same reason.
    /// </summary>
    private async Task AddFileAsync(ZipArchive zip, string name, string path, CancellationToken ct)
    {
        try
        {
            await using var reading = File.OpenRead(path);
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            await using var writing = entry.Open();
            await reading.CopyToAsync(writing, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't add {Path} to the export; skipping it.", path);
        }
    }

    /// <summary>A stored <c>ImagePath</c> as a zip folder: forward slashes, and no segment that could
    /// walk out of the archive when it's extracted. The paths we generate contain nothing of the sort —
    /// but this string is the one place a stored value becomes a path on someone ELSE'S machine, and the
    /// recipe names beside it are sanitised for exactly that reason.</summary>
    private static string ZipFolderFor(string imagePath) =>
        string.Join('/', imagePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not ("." or "..")));

    /// <summary>
    /// The recipes as they were read aloud, named for what they say.
    ///
    /// Only clips already in the cache are included — this exports, it doesn't synthesize, so asking for
    /// your data never spends your AI budget. A recipe you never had read to you simply has no audio, and
    /// that's the honest answer rather than a surprise bill.
    /// </summary>
    private async Task AddRecipeAudioAsync(
        ZipArchive zip, IReadOnlyList<Recipe> recipes, IReadOnlyList<RecipeStep> steps, CancellationToken ct)
    {
        if (speechCache is null) return;
        var householdId = await currentHousehold.GetIdAsync(ct);
        if (householdId is null) return;

        var stepsByRecipe = steps.GroupBy(s => s.RecipeId).ToDictionary(g => g.Key, g => g.ToList());
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var recipe in recipes)
        {
            // Asked by parts, not by grafting steps onto the Recipe: these are the same instances that
            // were just serialized into data.json, and quietly mutating them would leave this method
            // correct only for as long as it happens to run last.
            var mine = stepsByRecipe.TryGetValue(recipe.Id, out var found) ? found : [];
            var segments = RecipeNarration.Of(recipe.Name, recipe.Blurb, mine);
            var folder = UniqueFolderFor(recipe, used);

            for (var i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var clip = await speechCache.FindAsync(
                    householdId, segments[i].Text, RecipeNarration.ContextAt(segments, i), ct);
                if (clip is null) continue; // never synthesized, or trimmed since

                var entry = zip.CreateEntry(
                    $"recipes/{folder}/{segments[i].Name}{ExtensionFor(clip.MediaType)}",
                    CompressionLevel.NoCompression); // audio is already compressed
                await using var writing = entry.Open();
                await writing.WriteAsync(clip.Audio, ct);
            }
        }
    }

    /// <summary>What a zip entry may not contain if the archive is to extract anywhere. Deliberately NOT
    /// Path.GetInvalidFileNameChars(), which answers for the machine we're running on: Linux would happily
    /// let ':' and '*' through, and the resulting archive wouldn't unpack on the Windows box that asked
    /// for it. A download is a portable artifact, so it gets the strictest set.</summary>
    private static readonly SearchValues<char> UnsafeInNames = SearchValues.Create("<>:\"/\\|?*");

    /// <summary>A recipe's folder name: its own name where that survives being a filename, its id when it
    /// doesn't, and its id appended when two recipes would otherwise collide (variants share a name by
    /// design — "Chicken Thighs" adapted twice is two folders, not one that overwrites itself).</summary>
    private static string UniqueFolderFor(Recipe recipe, HashSet<string> used)
    {
        var cleaned = new string([.. recipe.Name.Select(c => UnsafeInNames.Contains(c) || char.IsControl(c) ? '-' : c)])
            // Trailing dots and spaces are legal in a zip and unopenable once extracted on Windows; a name
            // of nothing but dots would otherwise leave a segment that reads as "the folder above".
            .Trim().Trim('.').Trim();
        var name = string.IsNullOrWhiteSpace(cleaned) ? $"recipe-{recipe.Id}" : cleaned;
        if (!used.Add(name))
        {
            name = $"{name} ({recipe.Id})";
            used.Add(name);
        }
        return name;
    }

    private static string ExtensionFor(string mediaType) => mediaType switch
    {
        "audio/mpeg" => ".mp3",
        "audio/wav" or "audio/wave" => ".wav",
        "audio/ogg" => ".ogg",
        "audio/webm" => ".webm",
        _ => ".audio",
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Total rows the delete would remove — shown in the confirm dialog so the warning is
    /// concrete ("this removes 214 records") rather than vague.</summary>
    public async Task<int> CountAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Products.CountAsync(ct)
            + await db.PurchaseEvents.CountAsync(ct)
            + await db.InventorySignals.CountAsync(ct)
            + await db.ProductTags.CountAsync(ct)
            + await db.ProductSubstitutes.CountAsync(ct)
            + await db.ProductAliases.CountAsync(ct)
            + await db.Receipts.CountAsync(ct)
            + await db.ReceiptLines.CountAsync(ct)
            + await db.Recipes.CountAsync(ct)
            + await db.RecipeIngredients.CountAsync(ct)
            + await db.RecipeSteps.CountAsync(ct)
            + await db.ExcludedFoods.CountAsync(ct)
            + await db.GroceryExtras.CountAsync(ct)
            + await db.AppSettings.CountAsync(s => SettingKeys.UserContent.Contains(s.Key), ct);
    }

    /// <summary>Wipe every user-content table. In one transaction so it's all-or-nothing, and children
    /// before parents — ExecuteDelete is raw SQL, so it doesn't cascade and SQLite's FK enforcement would
    /// otherwise reject deleting a parent (e.g. a Product) while its rows still reference it.</summary>
    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Read the pointers to the saved receipt images BEFORE the rows that hold them are deleted.
        // ImagePath is the only reference to a receipt's folder, so deleting the rows first would strand
        // the images beyond any reach — which is exactly the bug this method used to have.
        var imagePaths = await db.Receipts.AsNoTracking().Select(r => r.ImagePath).ToListAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.RecipeSteps.ExecuteDeleteAsync(ct);
        await db.RecipeIngredients.ExecuteDeleteAsync(ct);
        await db.Recipes.ExecuteDeleteAsync(ct);
        await db.ReceiptLines.ExecuteDeleteAsync(ct);
        await db.PurchaseEvents.ExecuteDeleteAsync(ct);   // references both Product and Receipt
        await db.InventorySignals.ExecuteDeleteAsync(ct);
        await db.ProductTags.ExecuteDeleteAsync(ct);
        await db.ProductSubstitutes.ExecuteDeleteAsync(ct);
        await db.ProductAliases.ExecuteDeleteAsync(ct);
        await db.Receipts.ExecuteDeleteAsync(ct);
        await db.Products.ExecuteDeleteAsync(ct);
        await db.ExcludedFoods.ExecuteDeleteAsync(ct);
        await db.GroceryExtras.ExecuteDeleteAsync(ct);

        // The settings table is mostly configuration, which survives — but some of its keys hold content
        // derived from the pantry we just deleted (the last recipe ideas; the self-eval's per-receipt
        // scores, each named for its merchant and date). SettingKeys says which is which.
        await db.AppSettings.Where(s => SettingKeys.UserContent.Contains(s.Key)).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);

        // Only AFTER the rows are certainly gone. Both of these are derived from them, so deleting either
        // first would mean a failed transaction left the receipts listed but imageless, or the recipes
        // intact but silent.
        await DeleteSavedReceiptImagesAsync(imagePaths, ct);
        await DeleteCachedSpeechAsync(ct);
    }

    /// <summary>
    /// The saved copy of every receipt: a photograph of this household's shopping, and theirs as surely as
    /// the rows extracted from it. Removed two ways on purpose. The household's whole tree goes, which
    /// covers everything filed since receipts became household-scoped and can't miss a row; and each
    /// receipt's own <c>ImagePath</c> goes, which reaches the older rows whose folders predate that tree.
    /// Neither can fail the delete — the data itself is already gone, and reporting failure would only
    /// invite the user to press it again.
    /// </summary>
    private async Task DeleteSavedReceiptImagesAsync(IEnumerable<string> imagePaths, CancellationToken ct)
    {
        foreach (var imagePath in imagePaths)
        {
            receiptStorage.DeleteFolder(imagePath);
        }
        await receiptStorage.DeleteHouseholdAsync(ct);
    }

    /// <summary>
    /// The synthesized audio of this household's recipe steps. It's a recording of their content, so
    /// leaving it on disk after "delete my data" would make that button a lie — and it's the reason the
    /// speech cache is per-household rather than shared: a clip you can't attribute is a clip you can't
    /// delete. Failing to remove it must not fail the delete: the data itself is already gone, and
    /// reporting failure would invite them to press it again.
    /// </summary>
    private async Task DeleteCachedSpeechAsync(CancellationToken ct)
    {
        if (speechCache is null) return;

        var householdId = await currentHousehold.GetIdAsync(ct);
        if (householdId is null) return;

        if (!speechCache.DeleteHousehold(householdId))
            logger.LogWarning("Deleted the household's data, but some of its cached speech remains on disk.");
    }
}

/// <summary>A flat snapshot of everything in a household's database, for the JSON export/backup.</summary>
public sealed class DataExport
{
    public DateTimeOffset ExportedAt { get; init; }

    /// <summary>How the household set the app up, AND the pantry-derived things filed here (their last
    /// recipe ideas, their receipts' self-eval scores). Both are theirs; the export doesn't editorialise
    /// about which. Contains no keys — BYOK credentials live in the browser and never reach this table.</summary>
    public IReadOnlyList<AppSetting> Settings { get; init; } = [];

    /// <summary>What their AI features have spent, per day. Not removed by "delete my data" (that would
    /// make the button a quota reset), which is exactly why it has to be readable here.</summary>
    public IReadOnlyList<AiUsage> AiUsage { get; init; } = [];

    public IReadOnlyList<Product> Products { get; init; } = [];
    public IReadOnlyList<PurchaseEvent> Purchases { get; init; } = [];
    public IReadOnlyList<InventorySignal> Signals { get; init; } = [];
    public IReadOnlyList<ProductTag> Tags { get; init; } = [];
    public IReadOnlyList<ProductSubstitute> Substitutes { get; init; } = [];
    public IReadOnlyList<ProductAlias> Aliases { get; init; } = [];
    public IReadOnlyList<Receipt> Receipts { get; init; } = [];
    public IReadOnlyList<ReceiptLine> ReceiptLines { get; init; } = [];
    public IReadOnlyList<Recipe> Recipes { get; init; } = [];
    public IReadOnlyList<RecipeIngredient> RecipeIngredients { get; init; } = [];
    public IReadOnlyList<RecipeStep> RecipeSteps { get; init; } = [];
    public IReadOnlyList<ExcludedFood> ExcludedFoods { get; init; } = [];
    public IReadOnlyList<GroceryExtra> GroceryExtras { get; init; } = [];
}
