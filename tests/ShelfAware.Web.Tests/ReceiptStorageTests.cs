using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The store that owns where receipt images live. Its two jobs are both load-bearing: a delete has to be
/// able to FIND every image (or "delete my data" is a false statement), and it must never follow a stored
/// string OUT of the store and remove something that isn't a receipt.
/// </summary>
public class ReceiptStorageTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeCurrentHousehold _household = new();

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private ReceiptStorage Storage() => new(
        new AppPaths(_dataDir, Path.Combine(_dataDir, "receipts")),
        _household,
        NullLogger<ReceiptStorage>.Instance);

    [Fact]
    public async Task A_new_folder_is_stored_with_a_portable_separator()
    {
        // This string goes in the database. Path.Combine would bake in a backslash on Windows, and a
        // backslash is an ordinary filename character on Linux — so the Azure target would read the whole
        // thing as one literal filename and report every receipt's copy as missing.
        var imagePath = await Storage().NewFolderAsync();

        Assert.DoesNotContain('\\', imagePath);
        Assert.StartsWith("receipts/", imagePath);
    }

    [Fact]
    public async Task A_path_written_by_the_other_platform_still_finds_its_pages()
    {
        // The migration this rescues: rows written on the Windows self-host, read on Linux.
        var storage = Storage();
        var imagePath = await storage.NewFolderAsync();
        await storage.WritePageAsync(imagePath, 0, [1, 2, 3], "image/jpeg");

        var windowsStyle = imagePath.Replace('/', '\\');
        var unixStyle = imagePath.Replace('\\', '/');

        Assert.True(storage.HasPages(windowsStyle));
        Assert.True(storage.HasPages(unixStyle));
    }

    [Fact]
    public async Task Pages_come_back_in_page_order()
    {
        var storage = Storage();
        var imagePath = await storage.NewFolderAsync();
        await storage.WritePageAsync(imagePath, 0, [1], "image/jpeg");
        await storage.WritePageAsync(imagePath, 1, [2], "image/png");

        var pages = storage.Pages(imagePath);

        Assert.Equal(2, pages.Count);
        Assert.EndsWith("page-0.jpg", pages[0]);
        Assert.EndsWith("page-1.png", pages[1]);
    }

    [Fact]
    public async Task A_page_round_trips_with_the_media_type_it_was_saved_under()
    {
        var storage = Storage();
        var imagePath = await storage.NewFolderAsync();
        await storage.WritePageAsync(imagePath, 0, [9, 9], "application/pdf");

        var (bytes, mediaType) = await storage.ReadPageAsync(storage.Pages(imagePath).Single());

        Assert.Equal<byte[]>([9, 9], bytes);
        Assert.Equal("application/pdf", mediaType);
    }

    [Fact]
    public void A_path_outside_the_store_is_never_touched()
    {
        // The demo seeder files rows with a placeholder ImagePath. A delete driven by stored strings must
        // not follow one out of the store.
        var outside = Path.Combine(_dataDir, "demo");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "no-image"), "not a receipt");

        Storage().DeleteFolder("demo/no-image");

        Assert.True(File.Exists(Path.Combine(outside, "no-image")));
    }

    [Fact]
    public void Escaping_the_store_with_dot_dot_is_refused()
    {
        var outside = Path.Combine(_dataDir, "secrets");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "important");

        Storage().DeleteFolder("receipts/../secrets");

        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void The_store_root_itself_is_never_deletable()
    {
        // Being handed the root must not mean "delete every household's receipts" — hence strictly-inside
        // rather than at-or-inside.
        var root = Path.Combine(_dataDir, "receipts");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "someone-elses.audio"), "x");

        Storage().DeleteFolder("receipts");

        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public async Task Deleting_the_household_removes_its_whole_tree()
    {
        var storage = Storage();
        var imagePath = await storage.NewFolderAsync();
        await storage.WritePageAsync(imagePath, 0, [1], "image/jpeg");

        await storage.DeleteHouseholdAsync();

        Assert.False(storage.HasPages(imagePath));
    }

    [Fact]
    public async Task One_households_delete_leaves_anothers_images_alone()
    {
        var storage = Storage();
        _household.UseFixed("hh-a");
        var mine = await storage.NewFolderAsync();
        await storage.WritePageAsync(mine, 0, [1], "image/jpeg");

        _household.UseFixed("hh-b");
        var theirs = await storage.NewFolderAsync();
        await storage.WritePageAsync(theirs, 0, [2], "image/jpeg");

        await storage.DeleteHouseholdAsync(); // still scoped to hh-b

        Assert.False(storage.HasPages(theirs));
        Assert.True(storage.HasPages(mine));
    }
}
