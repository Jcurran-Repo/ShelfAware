using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

// The recipe-photo store — the security-sensitive half of the cookbook's photo feature. Save round-trips
// through read; the Within guard refuses a path that escapes the store (a malformed row must never read
// or delete an arbitrary file); and the per-household tree delete is "delete my data"'s reach, one
// household's wipe leaving another's photos alone.
public sealed class RecipeImageStorageTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-recipe-images", Guid.NewGuid().ToString("N"));

    private RecipeImageStorage Storage(string household = "hh-a") => new(
        new AppPaths(_dataDir, Path.Combine(_dataDir, "receipts")),
        new FakeCurrentHousehold(household),
        NullLogger<RecipeImageStorage>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Save_then_read_round_trips_the_bytes()
    {
        var storage = Storage();
        var path = await storage.SaveAsync([1, 2, 3], "image/jpeg");

        var read = await storage.ReadAsync(path);
        Assert.NotNull(read);
        Assert.Equal([1, 2, 3], read.Value.Bytes);
        Assert.Equal("image/jpeg", read.Value.MediaType);
    }

    [Fact]
    public async Task Saved_path_is_relative_household_scoped_and_forward_slashed()
    {
        var path = await Storage().SaveAsync([1], "image/jpeg");
        Assert.StartsWith("recipe-images/", path);
        Assert.DoesNotContain('\\', path); // forward slashes — portable across OSes in the DB
        Assert.EndsWith(".jpg", path);
    }

    [Fact]
    public async Task Replacing_mints_a_new_filename_so_the_url_busts_cache()
    {
        var storage = Storage();
        Assert.NotEqual(await storage.SaveAsync([1], "image/jpeg"), await storage.SaveAsync([2], "image/jpeg"));
    }

    [Fact]
    public async Task Read_refuses_a_path_that_escapes_the_store()
    {
        // Plant a file OUTSIDE the recipe-images store, then try to reach it via a traversal path. The
        // guard must refuse it (return null) even though the file genuinely exists.
        Directory.CreateDirectory(_dataDir);
        var outside = Path.Combine(_dataDir, "secret.txt");
        await File.WriteAllTextAsync(outside, "nope");

        Assert.True(File.Exists(outside));
        Assert.Null(await Storage().ReadAsync("recipe-images/../secret.txt"));
    }

    [Fact]
    public async Task Delete_removes_one_image()
    {
        var storage = Storage();
        var path = await storage.SaveAsync([1], "image/jpeg");
        Assert.NotNull(await storage.ReadAsync(path));

        storage.Delete(path);
        Assert.Null(await storage.ReadAsync(path));
    }

    [Fact]
    public async Task Delete_household_forgets_every_image()
    {
        var storage = Storage();
        var a = await storage.SaveAsync([1], "image/jpeg");
        var b = await storage.SaveAsync([2], "image/jpeg");

        await storage.DeleteHouseholdAsync();

        Assert.Null(await storage.ReadAsync(a));
        Assert.Null(await storage.ReadAsync(b));
    }

    [Fact]
    public async Task One_households_delete_leaves_anothers_images_alone()
    {
        var a = Storage("hh-a");
        var b = Storage("hh-b");
        var aPath = await a.SaveAsync([1], "image/jpeg");
        var bPath = await b.SaveAsync([2], "image/jpeg");

        await a.DeleteHouseholdAsync();

        Assert.Null(await a.ReadAsync(aPath));    // A's is gone
        Assert.NotNull(await b.ReadAsync(bPath)); // B's is untouched
    }
}
