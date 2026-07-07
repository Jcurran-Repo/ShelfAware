using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The auth-side twin of <see cref="TestDb"/>: a real in-memory SQLite AuthDbContext, so the unique
/// invite-code index and the Identity schema behave exactly as production.
/// </summary>
internal sealed class TestAuthDb : IDbContextFactory<AuthDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AuthDbContext> _options;

    public TestAuthDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public AuthDbContext CreateDbContext() => new(_options);

    public Task<AuthDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public void Dispose() => _connection.Dispose();
}
