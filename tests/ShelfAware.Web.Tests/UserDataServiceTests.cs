using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Speech;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

public class UserDataServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeCurrentHousehold _household = new();
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-web-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private ReceiptStorage Storage() => new(
        new AppPaths(_dataDir, Path.Combine(_dataDir, "receipts")),
        _household,
        NullLogger<ReceiptStorage>.Instance);

    private UserDataService Service(ISpeechCache? speech = null) =>
        new(_db, _household, Storage(), speech, NullLogger<UserDataService>.Instance);

    /// <summary>Stands in for the disk cache: answers only for text it was told about, so a test can say
    /// "this step has audio and that one doesn't" without synthesizing anything.</summary>
    private sealed class FakeSpeechCache : ISpeechCache
    {
        private readonly Dictionary<string, byte[]> _clips = new();
        public string? DeletedHousehold { get; private set; }

        public void Add(string text, byte[] audio) => _clips[text] = audio;

        public Task<StoredClip?> FindAsync(
            string householdId, string text, SpeechContext? context = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_clips.TryGetValue(text, out var audio) ? new StoredClip(audio, "audio/mpeg") : null);

        public bool DeleteHousehold(string householdId)
        {
            DeletedHousehold = householdId;
            return true;
        }
    }

    // One row (or two) in every user-content table, including FK children, so a wrong delete order or a
    // missed table shows up.
    private async Task Seed()
    {
        await using var db = _db.CreateDbContext();
        var milk = new Product
        {
            Name = "Whole Milk",
            Purchases = { new PurchaseEvent { PurchasedAt = new DateOnly(2026, 7, 1) } },
            Signals = { new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = DateTimeOffset.Now } },
            Tags = { new ProductTag { Value = "Dairy" } },
            Substitutes = { new ProductSubstitute { Value = "milk" } },
        };
        db.Products.Add(milk);
        await db.SaveChangesAsync(); // assigns milk.Id so the alias FK resolves
        db.ProductAliases.Add(new ProductAlias { Merchant = "Walmart", RawText = "GV MLK", ProductId = milk.Id });
        var cereal = new Recipe
        {
            Name = "Cereal",
            Ingredients = { new RecipeIngredient { Name = "milk", IsMain = true } },
            Steps = { new RecipeStep { Order = 1, Text = "Pour the milk" } },
        };
        db.Recipes.Add(cereal);
        db.MealEvents.Add(new MealEvent { Recipe = cereal, AteAt = new DateOnly(2026, 7, 10) });
        db.Receipts.Add(new Receipt
        {
            ImagePath = "receipts/x",
            Lines = { new ReceiptLine { RawText = "GV MLK", NormalizedName = "Whole Milk" } },
        });
        db.ExcludedFoods.Add(new ExcludedFood { Value = "olives" });
        db.GroceryExtras.Add(new GroceryExtra { Name = "napkins" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DeleteAllAsync_empties_every_user_table()
    {
        await Seed();
        var svc = Service();
        Assert.True(await svc.CountAllAsync() > 0);

        await svc.DeleteAllAsync();

        Assert.Equal(0, await svc.CountAllAsync());
        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Products.CountAsync());
        Assert.Equal(0, await db.PurchaseEvents.CountAsync());
        Assert.Equal(0, await db.Recipes.CountAsync());
        Assert.Equal(0, await db.RecipeIngredients.CountAsync());
        Assert.Equal(0, await db.MealEvents.CountAsync());
        Assert.Equal(0, await db.Receipts.CountAsync());
        Assert.Equal(0, await db.ReceiptLines.CountAsync());
        Assert.Equal(0, await db.GroceryExtras.CountAsync());
    }

    [Fact]
    public async Task DeleteAllAsync_on_an_empty_db_is_a_no_op()
    {
        var svc = Service();
        await svc.DeleteAllAsync(); // must not throw on nothing to delete
        Assert.Equal(0, await svc.CountAllAsync());
    }

    [Fact]
    public async Task DeleteAllAsync_removes_the_saved_receipt_images_too()
    {
        // The saved copy of a receipt is a photograph of the household's shopping. Deleting the rows and
        // leaving it on disk made "delete my data" a false statement — and because ImagePath was the only
        // pointer to the folder, the same delete put it beyond reach of any later cleanup.
        var storage = Storage();
        var imagePath = await storage.NewFolderAsync();
        await storage.WritePageAsync(imagePath, 0, [1, 2, 3], "image/jpeg");
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt { ImagePath = imagePath });
            await db.SaveChangesAsync();
        }
        Assert.True(storage.HasPages(imagePath));

        await Service().DeleteAllAsync();

        Assert.False(storage.HasPages(imagePath));
        Assert.False(Directory.Exists(Path.Combine(_dataDir, imagePath)));
    }

    [Fact]
    public async Task DeleteAllAsync_reaches_images_filed_before_receipts_were_household_scoped()
    {
        // Older rows point at "receipts/<folder>" with no household segment, so they don't live under the
        // household's tree. The stored pointer is what reaches them.
        var legacyPath = Path.Combine("receipts", "20260101-000000-legacy");
        var folder = Path.Combine(_dataDir, legacyPath);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "page-0.jpg"), [1, 2, 3]);
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt { ImagePath = legacyPath });
            await db.SaveChangesAsync();
        }

        await Service().DeleteAllAsync();

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task DeleteAllAsync_leaves_alone_a_path_outside_the_receipts_store()
    {
        // The demo seeder files rows with a placeholder ImagePath. A delete driven by stored strings must
        // not follow one out of the store and remove something that isn't a receipt.
        var outside = Path.Combine(_dataDir, "demo");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "no-image"), "not a receipt");
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt { ImagePath = "demo/no-image" });
            await db.SaveChangesAsync();
        }

        await Service().DeleteAllAsync();

        Assert.True(File.Exists(Path.Combine(outside, "no-image")));
    }

    [Fact]
    public async Task DeleteAllAsync_removes_pantry_derived_settings_but_keeps_configuration()
    {
        await using (var db = _db.CreateDbContext())
        {
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.ReceiptFolder, Value = @"C:\receipts" });
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.ImportMode, Value = "Smart" });
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.LastRecipeSuggestions, Value = "[{\"name\":\"Chicken\"}]" });
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.SelfEvalResults, Value = "{\"Fixtures\":[{\"Name\":\"Walmart 2026-07-04\"}]}" });
            await db.SaveChangesAsync();
        }

        await Service().DeleteAllAsync();

        await using var check = _db.CreateDbContext();
        var left = await check.AppSettings.Select(s => s.Key).ToListAsync();

        // Their recipe ideas and their receipts' merchant names are content, and content goes...
        Assert.DoesNotContain(SettingKeys.LastRecipeSuggestions, left);
        Assert.DoesNotContain(SettingKeys.SelfEvalResults, left);
        // ...but wiping the pantry shouldn't forget which folder the receipts arrive in.
        Assert.Contains(SettingKeys.ReceiptFolder, left);
        Assert.Contains(SettingKeys.ImportMode, left);
    }

    [Fact]
    public async Task ExportAsync_returns_the_stored_content()
    {
        await Seed();
        var svc = Service();

        var export = await svc.ExportAsync();

        Assert.Single(export.Products);
        Assert.Equal("Whole Milk", export.Products[0].Name);
        Assert.Single(export.Recipes);
        Assert.Single(export.MealEvents);
        Assert.Single(export.Receipts);
        Assert.Single(export.GroceryExtras);
    }

    [Fact]
    public async Task ExportAsync_includes_settings_and_usage_not_just_pantry_rows()
    {
        // "What do you have on me" has one defensible answer, and it isn't "most of it". Note these two
        // are treated differently by DELETE (config survives; usage survives so a wipe isn't a quota
        // reset) — which is the reason they have to be readable here.
        await using (var db = _db.CreateDbContext())
        {
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.ReceiptFolder, Value = @"C:\receipts" });
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.LastRecipeSuggestions, Value = "[{\"name\":\"Chicken\"}]" });
            db.AiUsages.Add(new AiUsage { Day = new DateOnly(2026, 7, 15), Calls = 3, InputTokens = 100, OutputTokens = 20 });
            await db.SaveChangesAsync();
        }

        var export = await Service().ExportAsync();

        Assert.Equal(2, export.Settings.Count);
        Assert.Contains(export.Settings, s => s.Key == SettingKeys.LastRecipeSuggestions);
        Assert.Contains(export.Settings, s => s.Key == SettingKeys.ReceiptFolder);
        Assert.Equal(3, Assert.Single(export.AiUsage).Calls);
    }

    private async Task<ZipArchive> ArchiveAsync(ISpeechCache? speech = null)
    {
        var buffer = new MemoryStream();
        await Service(speech).WriteArchiveAsync(buffer);
        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    [Fact]
    public async Task The_archive_needs_a_stream_that_tolerates_synchronous_writes()
    {
        // ZipArchive is a synchronous API: it writes its data descriptors and central directory with
        // Stream.Write. A MemoryStream doesn't care, which is why every test above passes — but Kestrel's
        // response stream throws on sync IO unless the endpoint opts in, so this passed in tests and
        // broke the moment a browser asked for it. Pinning the requirement so the endpoint's
        // AllowSynchronousIO can never be "tidied away" without something going red.
        await Seed();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service().WriteArchiveAsync(new SynchronousWritesRefusedStream()));
    }

    /// <summary>Behaves like Kestrel's response stream with AllowSynchronousIO off.</summary>
    private sealed class SynchronousWritesRefusedStream : Stream
    {
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous operations are disallowed.");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_archive_carries_the_json_and_the_receipt_images_beside_it()
    {
        // The point of the zip: a Receipt row in data.json names the folder its photo is in, so the
        // export is something you can open rather than only parse.
        var storage = Storage();
        var imagePath = await storage.NewFolderAsync();
        await storage.WritePageAsync(imagePath, 0, [1, 2, 3], "image/jpeg");
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt { ImagePath = imagePath, Merchant = "Walmart" });
            await db.SaveChangesAsync();
        }

        using var zip = await ArchiveAsync();

        Assert.NotNull(zip.GetEntry("data.json"));
        var image = Assert.Single(zip.Entries, e => e.FullName.EndsWith("page-0.jpg"));
        Assert.StartsWith(imagePath, image.FullName);
    }

    [Fact]
    public async Task A_page_that_cannot_be_read_costs_that_page_and_not_the_whole_download()
    {
        // By the time images are written the response has started, so an escaping IOException can't become
        // an error page — it just drops the connection and leaves a truncated zip. One locked file must
        // cost one file.
        var storage = Storage();
        var readable = await storage.NewFolderAsync();
        await storage.WritePageAsync(readable, 0, [1, 2, 3], "image/jpeg");
        var locked = await storage.NewFolderAsync();
        await storage.WritePageAsync(locked, 0, [4, 5, 6], "image/jpeg");
        await using (var db = _db.CreateDbContext())
        {
            db.Receipts.Add(new Receipt { ImagePath = locked });
            db.Receipts.Add(new Receipt { ImagePath = readable });
            await db.SaveChangesAsync();
        }

        // Hold the file open exclusively — what a scanner or a mid-write importer looks like.
        var lockedPage = storage.Pages(locked).Single();
        using (File.Open(lockedPage, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            using var zip = await ArchiveAsync();

            Assert.NotNull(zip.GetEntry("data.json"));
            Assert.Single(zip.Entries, e => e.FullName.StartsWith(readable) && e.FullName.EndsWith("page-0.jpg"));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith(locked));
        }
    }

    [Fact]
    public async Task Exporting_does_not_disturb_the_rows_it_just_serialized()
    {
        // The audio pass used to graft steps back onto the very Recipe instances that had just been
        // serialized, which was correct only for as long as it ran last.
        var speech = new FakeSpeechCache();
        await using (var db = _db.CreateDbContext())
        {
            db.Recipes.Add(new Recipe { Name = "Toast", Steps = { new RecipeStep { Order = 1, Text = "Toast it." } } });
            await db.SaveChangesAsync();
        }

        var snapshot = await Service(speech).ExportAsync();
        var buffer = new MemoryStream();
        await Service(speech).WriteArchiveAsync(buffer);

        // The flat load is what keeps data.json cycle-free; nothing in the archive path may undo it.
        Assert.Empty(Assert.Single(snapshot.Recipes).Steps);
    }

    [Fact]
    public async Task Recipe_audio_is_named_for_the_recipe_and_step_it_speaks()
    {
        // A content-addressed hash is honest and useless. The export re-derives each segment's key the
        // same way the reader did, so the files come out named for what they say.
        var speech = new FakeSpeechCache();
        speech.Add("Chicken Thighs. Weeknight easy.", [1]);
        speech.Add("Step 1. Heat the oven.", [2]);
        // Step 2 deliberately has no clip: it was never read aloud.
        await using (var db = _db.CreateDbContext())
        {
            db.Recipes.Add(new Recipe
            {
                Name = "Chicken Thighs",
                Blurb = "Weeknight easy.",
                Steps =
                {
                    new RecipeStep { Order = 1, Text = "Heat the oven." },
                    new RecipeStep { Order = 2, Text = "Roast for 40 minutes." },
                },
            });
            await db.SaveChangesAsync();
        }

        using var zip = await ArchiveAsync(speech);

        Assert.NotNull(zip.GetEntry("recipes/Chicken Thighs/intro.mp3"));
        Assert.NotNull(zip.GetEntry("recipes/Chicken Thighs/step-1.mp3"));
        Assert.Null(zip.GetEntry("recipes/Chicken Thighs/step-2.mp3"));
    }

    [Fact]
    public async Task An_export_never_synthesizes_audio_it_does_not_already_have()
    {
        // Asking for your data must not spend your AI budget. A recipe nobody ever had read aloud simply
        // has no audio in the archive.
        await using (var db = _db.CreateDbContext())
        {
            db.Recipes.Add(new Recipe { Name = "Toast", Steps = { new RecipeStep { Order = 1, Text = "Toast it." } } });
            await db.SaveChangesAsync();
        }

        using var zip = await ArchiveAsync(new FakeSpeechCache()); // knows about nothing

        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("recipes/"));
        Assert.NotNull(zip.GetEntry("data.json"));
    }

    [Fact]
    public async Task Two_recipes_sharing_a_name_get_their_own_folders()
    {
        // Variants share a name by design — an adapted "Chicken Thighs" is a sibling, not a replacement.
        var speech = new FakeSpeechCache();
        speech.Add("Chicken Thighs.", [1]);
        await using (var db = _db.CreateDbContext())
        {
            db.Recipes.Add(new Recipe { Name = "Chicken Thighs" });
            db.Recipes.Add(new Recipe { Name = "Chicken Thighs" });
            await db.SaveChangesAsync();
        }

        using var zip = await ArchiveAsync(speech);

        Assert.Equal(2, zip.Entries.Count(e => e.FullName.EndsWith("intro.mp3")));
    }

    [Theory]
    [InlineData("../../etc/passwd")]  // the classic
    [InlineData("..")]                // nothing left after cleaning
    [InlineData("C:\\evil")]          // a drive-qualified path
    [InlineData("   ")]               // no name at all
    public async Task A_recipe_name_can_never_produce_an_entry_that_escapes_on_extraction(string hostile)
    {
        // A recipe name is user input that ends up as a path inside a file someone will unzip. What
        // matters isn't that ".." never appears — "-..-etc-passwd" is one harmless folder — it's that no
        // SEGMENT is a traversal, so the archive can't write outside where it was extracted.
        var speech = new FakeSpeechCache();
        await using (var db = _db.CreateDbContext())
        {
            db.Recipes.Add(new Recipe { Name = hostile });
            await db.SaveChangesAsync();
        }
        // Whatever the intro turned out to be, this cache has it — so an entry is definitely produced.
        var intro = RecipeNarration.Of(new Recipe { Name = hostile }).First().Text;
        speech.Add(intro, [1]);

        using var zip = await ArchiveAsync(speech);

        var entry = Assert.Single(zip.Entries, e => e.FullName.EndsWith("intro.mp3"));
        var segments = entry.FullName.Split('/');
        Assert.Equal(3, segments.Length);                 // recipes / <one folder> / intro.mp3
        Assert.Equal("recipes", segments[0]);
        Assert.DoesNotContain(segments, s => s is "." or ".." || string.IsNullOrWhiteSpace(s));
    }

    [Fact]
    public async Task The_archive_works_with_no_speech_cache_at_all()
    {
        // Speech:CacheMegabytes = 0 means there is no ISpeechCache to inject. The export still works.
        await Seed();

        using var zip = await ArchiveAsync(speech: null);

        Assert.NotNull(zip.GetEntry("data.json"));
    }

    [Fact]
    public void Every_table_in_the_household_database_is_in_the_export()
    {
        // The export promises "everything", and a promise nothing checks is a comment. Adding a DbSet
        // without a matching export list fails here rather than quietly shipping a partial download.
        var tables = typeof(ShelfAwareDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .ToList();

        var exported = typeof(DataExport).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .ToHashSet();

        Assert.NotEmpty(tables); // guards against the reflection silently matching nothing
        var missing = tables.Where(t => !exported.Contains(t)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0,
            $"Not exported: {string.Join(", ", missing)}. Every table in a household's database belongs in " +
            "the download — add an IReadOnlyList<T> to DataExport and populate it in ExportAsync.");
    }
}
