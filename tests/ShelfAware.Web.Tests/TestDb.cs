using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>
/// A real SQLite database in memory, alive for the life of the (kept-open) connection, shared by every
/// context the factory hands out. Real EF + real SQLite — FK enforcement, unique indexes, and cascade
/// behavior are exactly what production sees, which is the point: the persistence bugs this project
/// had (FK violation on delete, unique-alias-index blowups) don't reproduce on fakes.
/// </summary>
internal sealed class TestDb : IDbContextFactory<ShelfAwareDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ShelfAwareDbContext> _options;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ShelfAwareDbContext>().UseSqlite(_connection).Options;
        using var db = CreateUnscopedContext();
        db.Database.EnsureCreated();
    }

    /// <summary>The household every context from this factory is scoped to. Defaults to one test
    /// household so existing suites run under the real v3 query filters + insert stamping without any
    /// call-site changes; isolation tests re-point it to simulate a second household.</summary>
    public string? HouseholdId { get; set; } = "hh-test";

    public ShelfAwareDbContext CreateDbContext() => new(_options) { HouseholdId = HouseholdId };

    public Task<ShelfAwareDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    /// <summary>A context with NO household — sees only ownerless rows, stamps nothing. For asserting
    /// what's physically in the tables regardless of tenancy.</summary>
    public ShelfAwareDbContext CreateUnscopedContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
