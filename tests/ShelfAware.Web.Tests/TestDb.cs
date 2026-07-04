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
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public ShelfAwareDbContext CreateDbContext() => new(_options);

    public Task<ShelfAwareDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public void Dispose() => _connection.Dispose();
}
