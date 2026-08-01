using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// A <see cref="DemoDataSeeder"/> wired to a throwaway receipts directory, because seeding writes a real
/// file: the sample pantry's one un-reviewed receipt ships with the image it was read from, and that
/// image goes through the real <see cref="ReceiptStorage"/> like any other. Faking the storage would
/// leave the seeder's own failure path — and the household-scoped path it files under — untested.
///
/// <para>Shared by both suites (the persistence tests and the bUnit page tests) so there is one
/// definition of "how you construct the seeder", the way <c>TestDb</c> is one definition of the DB.</para>
/// </summary>
internal sealed class DemoSeeding : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "shelfaware-demo-seed", Guid.NewGuid().ToString("N"));

    public ReceiptStorage Storage { get; }

    /// <param name="householdId">Must be the household the DB factory is scoped to. In production both
    /// sides read the SAME scoped <c>ICurrentHousehold</c>, so the image folder and the rows that point
    /// at it cannot disagree; a fixture that hardcoded its own id would keep passing if they ever did.
    /// Defaults to <c>TestDb</c>'s household so the two line up without every call site saying so.</param>
    public DemoSeeding(string householdId = "hh-test")
    {
        Storage = new ReceiptStorage(
            new AppPaths(_dataDir, Path.Combine(_dataDir, "receipts")),
            new FakeCurrentHousehold(householdId),
            NullLogger<ReceiptStorage>.Instance);
    }

    public DemoDataSeeder Seeder(IHouseholdDbFactory dbFactory) =>
        new(dbFactory, Storage, NullLogger<DemoDataSeeder>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }
}
