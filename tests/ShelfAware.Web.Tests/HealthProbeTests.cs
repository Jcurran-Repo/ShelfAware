using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The probe behind <c>/healthz</c>. Real in-memory SQLite for both databases, so "can connect" means what
/// it means in production rather than what a fake was told to say.
///
/// ⚠️ The endpoint is anonymous — it answers the whole internet — so the two properties that matter are
/// that it tells an operator WHICH database is unreachable, and that it tells everyone else nothing else.
/// </summary>
public sealed class HealthProbeTests : IDisposable
{
    private readonly TestDb _pantry = new();
    private readonly TestAuthDb _identity = new();

    public void Dispose()
    {
        _pantry.Dispose();
        _identity.Dispose();
    }

    private HealthProbe Probe(
        IDbContextFactory<AuthDbContext>? identity = null,
        IDbContextFactory<ShelfAwareDbContext>? pantry = null,
        ILogger<HealthProbe>? logger = null) =>
        new(identity ?? _identity, pantry ?? _pantry, logger ?? NullLogger<HealthProbe>.Instance);

    [Fact]
    public async Task Both_databases_reachable_reports_healthy()
    {
        var report = await Probe().CheckAsync();

        Assert.True(report.Healthy);
        Assert.Empty(report.Failing);
    }

    [Fact]
    public async Task An_unreachable_identity_database_is_named_and_the_pantry_is_not()
    {
        // Both halves: the failing one is reported, and the healthy one is NOT — a check that named both
        // whenever either failed would send an operator to the wrong file.
        var log = new CapturingHealthLogger();

        var report = await Probe(identity: new BrokenFactory<AuthDbContext>(), logger: log).CheckAsync();

        Assert.False(report.Healthy);
        Assert.Equal("identity", Assert.Single(report.Failing));
        Assert.NotEmpty(log.Errors); // the reason was logged, not swallowed
    }

    [Fact]
    public async Task An_unreachable_pantry_database_is_named_and_the_identity_is_not()
    {
        var report = await Probe(pantry: new BrokenFactory<ShelfAwareDbContext>()).CheckAsync();

        Assert.False(report.Healthy);
        Assert.Equal("pantry", Assert.Single(report.Failing));
    }

    [Fact]
    public async Task Both_unreachable_names_both()
    {
        var report = await Probe(
            identity: new BrokenFactory<AuthDbContext>(),
            pantry: new BrokenFactory<ShelfAwareDbContext>()).CheckAsync();

        Assert.False(report.Healthy);
        Assert.Equal(["identity", "pantry"], report.Failing);
    }

    [Fact]
    public async Task The_failure_it_publishes_carries_none_of_the_reason()
    {
        // ⚠️ This endpoint is anonymous, so its body is public. The exception text names a file path; the
        // report must carry the database's neutral name and nothing derived from the failure — the same
        // rule the AI surfaces follow, applied to the one surface that answers without a login.
        var broken = new BrokenFactory<AuthDbContext>();

        var report = await Probe(identity: broken).CheckAsync();

        var published = string.Join(" ", report.Failing);
        Assert.DoesNotContain(BrokenFactory<AuthDbContext>.SecretPath, published, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", published, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_check_inside_the_cache_window_does_not_ask_the_databases_again()
    {
        // The cache is what lets the endpoint stay unmetered: rate-limiting a health check is how a monitor
        // learns to report an outage that isn't happening. Asserted by BREAKING a database between the two
        // calls — the second answer can only still be healthy if nothing was re-read.
        var identity = new SwitchableFactory<AuthDbContext>(_identity);
        var probe = Probe(identity: identity);

        Assert.True((await probe.CheckAsync()).Healthy);
        identity.Broken = true;

        Assert.True((await probe.CheckAsync()).Healthy);
        Assert.Equal(1, identity.Created); // and it really was only asked once
    }

    /// <summary>A factory whose contexts point at a path that cannot be opened, so <c>CanConnectAsync</c>
    /// fails the way an unreadable database file does rather than by a stubbed boolean.</summary>
    private sealed class BrokenFactory<TContext> : IDbContextFactory<TContext> where TContext : DbContext
    {
        internal const string SecretPath = "/var/lib/shelfaware-that-is-not-there/secret.db";

        public TContext CreateDbContext() =>
            throw new InvalidOperationException($"could not open {SecretPath}");

        public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException($"could not open {SecretPath}");
    }

    private sealed class SwitchableFactory<TContext>(IDbContextFactory<TContext> inner)
        : IDbContextFactory<TContext> where TContext : DbContext
    {
        public bool Broken { get; set; }
        public int Created { get; private set; }

        public TContext CreateDbContext()
        {
            Created++;
            return Broken ? throw new InvalidOperationException("gone") : inner.CreateDbContext();
        }

        public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Created++;
            return Broken
                ? throw new InvalidOperationException("gone")
                : inner.CreateDbContextAsync(cancellationToken);
        }
    }

    private sealed class CapturingHealthLogger : ILogger<HealthProbe>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error) Errors.Add(formatter(state, exception));
        }
    }
}
