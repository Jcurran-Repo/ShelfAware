using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.Tests;

/// <summary>The in-app error log: what gets captured (Error+ only, never its own pipeline), how
/// occurrences of one error collapse into one counted row, how the table stays bounded, and how a
/// full or failing pipeline sheds load VISIBLY (counted drops) instead of blocking or throwing
/// into the app it observes.</summary>
public class ErrorLogTests : IDisposable
{
    private readonly TestAuthDb _authDb = new();

    public void Dispose() => _authDb.Dispose();

    private ErrorLogStore Store() => new(_authDb);

    private static CapturedError Event(
        string category = "ShelfAware.Web.Components.Pages.Home",
        string? exceptionType = "System.InvalidOperationException",
        string? template = "Loading failed for {Name}",
        string message = "Loading failed for Milk",
        DateTimeOffset? at = null) =>
        new(at ?? DateTimeOffset.Now, "Error", category, exceptionType, template, message, "detail");

    // ------------------------------------------------------------------------------- fingerprint

    [Fact]
    public void Occurrences_of_one_log_site_share_a_fingerprint_whatever_the_arguments_were()
    {
        var first = Event(message: "Loading failed for Milk");
        var second = Event(message: "Loading failed for Eggs");
        Assert.Equal(ErrorLogStore.FingerprintOf(first), ErrorLogStore.FingerprintOf(second));
    }

    [Fact]
    public void Category_exception_type_and_template_each_split_the_fingerprint()
    {
        var reference = ErrorLogStore.FingerprintOf(Event());
        Assert.NotEqual(reference, ErrorLogStore.FingerprintOf(Event(category: "Other.Category")));
        Assert.NotEqual(reference, ErrorLogStore.FingerprintOf(Event(exceptionType: "System.TimeoutException")));
        Assert.NotEqual(reference, ErrorLogStore.FingerprintOf(Event(template: "A different site {Name}")));
    }

    [Fact]
    public void Without_a_template_the_rendered_message_stands_in()
    {
        // Unstructured log calls still group by their constant text rather than collapsing into
        // one "no template" bucket.
        var a = ErrorLogStore.FingerprintOf(Event(template: null, message: "constant text A"));
        var b = ErrorLogStore.FingerprintOf(Event(template: null, message: "constant text B"));
        Assert.NotEqual(a, b);
    }

    // ------------------------------------------------------------------------------ record/trim

    [Fact]
    public async Task A_repeating_error_is_one_row_counting_up_with_the_freshest_message()
    {
        var store = Store();
        await store.RecordAsync(Event(message: "Loading failed for Milk", at: DateTimeOffset.Now.AddMinutes(-5)));
        await store.RecordAsync(Event(message: "Loading failed for Eggs"));

        var rows = await store.ListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(2, row.Count);
        Assert.Equal("Loading failed for Eggs", row.LastMessage);
        Assert.True(row.LastSeenAt > row.FirstSeenAt);
    }

    [Fact]
    public async Task Distinct_errors_are_distinct_rows_newest_activity_first()
    {
        var store = Store();
        await store.RecordAsync(Event(at: DateTimeOffset.Now.AddMinutes(-10)));
        await store.RecordAsync(Event(template: "Another site failed", message: "Another site failed"));

        var rows = await store.ListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Another site failed", rows[0].LastMessage);
    }

    [Fact]
    public async Task The_table_is_bounded_and_the_longest_quiet_row_is_trimmed_first()
    {
        // Fill to the cap directly (RecordAsync per row would be needlessly slow), then one more
        // real insert must trim exactly the quietest row.
        await using (var db = _authDb.CreateDbContext())
        {
            for (var i = 0; i < ErrorLogStore.MaxRows; i++)
            {
                db.ErrorLog.Add(new ErrorLogEntry
                {
                    Fingerprint = $"F{i:D4}", Level = "Error", Category = "Seed", LastMessage = $"seed {i}",
                    Count = 1,
                    FirstSeenAt = DateTimeOffset.Now.AddDays(-30),
                    LastSeenAt = DateTimeOffset.Now.AddMinutes(-ErrorLogStore.MaxRows + i),
                });
            }
            await db.SaveChangesAsync();
        }

        await Store().RecordAsync(Event());

        var rows = await Store().ListAsync();
        Assert.Equal(ErrorLogStore.MaxRows, rows.Count);
        Assert.Contains(rows, r => r.LastMessage == "Loading failed for Milk"); // the new arrival stayed
        Assert.DoesNotContain(rows, r => r.LastMessage == "seed 0"); // the quietest row paid for it
        Assert.Contains(rows, r => r.LastMessage == "seed 1");
    }

    // ---------------------------------------------------------------------------------- capture

    [Fact]
    public void The_provider_captures_errors_with_their_template_and_exception()
    {
        var sink = new ErrorLogSink();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new ErrorLogCaptureProvider(sink)));
        var logger = factory.CreateLogger("ShelfAware.Web.Components.Pages.Home");

        logger.LogError(new InvalidOperationException("boom"), "Loading failed for {Name}", "Milk");

        Assert.True(sink.Channel.Reader.TryRead(out var captured));
        Assert.Equal("Loading failed for {Name}", captured!.MessageTemplate);
        Assert.Equal("Loading failed for Milk", captured.Message);
        Assert.Equal("System.InvalidOperationException", captured.ExceptionType);
        Assert.Contains("boom", captured.ExceptionDetail);
        Assert.Equal("ShelfAware.Web.Components.Pages.Home", captured.Category);
    }

    [Fact]
    public void Warnings_are_not_captured_only_errors_and_worse()
    {
        var sink = new ErrorLogSink();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new ErrorLogCaptureProvider(sink)));
        var logger = factory.CreateLogger("ShelfAware.Web.Components.Pages.Home");

        logger.LogWarning("Copying to the clipboard failed.");
        Assert.False(sink.Channel.Reader.TryRead(out _));

        logger.LogCritical("The sky fell.");
        Assert.True(sink.Channel.Reader.TryRead(out var captured));
        Assert.Equal("Critical", captured!.Level);
    }

    [Fact]
    public void The_pipelines_own_categories_are_never_captured()
    {
        // The recursion break: the writer logs its troubles under Diagnostics, and a capture of
        // those would feed a failing writer back to itself forever.
        var sink = new ErrorLogSink();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new ErrorLogCaptureProvider(sink)));
        var logger = factory.CreateLogger(typeof(ErrorLogWriter).FullName!);

        logger.LogError("Couldn't persist a captured error event.");

        Assert.False(sink.Channel.Reader.TryRead(out _));
    }

    // ------------------------------------------------------------------------------- load-shed

    [Fact]
    public void A_full_sink_refuses_the_write_and_counts_the_drop()
    {
        var sink = new ErrorLogSink();
        for (var i = 0; i < ErrorLogSink.Capacity; i++)
        {
            Assert.True(sink.TryPost(Event(message: $"m{i}")));
        }

        Assert.False(sink.TryPost(Event(message: "one too many")));
        Assert.Equal(1, sink.Dropped);
    }

    [Fact]
    public void Capture_is_suppressed_inside_a_persist_scope_and_resumes_after()
    {
        var sink = new ErrorLogSink();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new ErrorLogCaptureProvider(sink)));
        var logger = factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        using (ErrorLogSink.BeginPersist())
        {
            logger.LogError("Failed executing DbCommand");
        }
        Assert.False(sink.Channel.Reader.TryRead(out _));

        logger.LogError("Failed executing DbCommand");
        Assert.True(sink.Channel.Reader.TryRead(out _)); // the scope ended with its using
    }

    [Fact]
    public async Task A_failing_persist_does_not_feed_efs_own_error_back_into_the_sink()
    {
        // The SECOND recursion road (the category exclusion covers only the first): a failing
        // persist makes EF itself log at Error under a category the capture watches. Real broken
        // table, real EF logging wired into the capture — without the writer's persist scope the
        // echo would re-post, landing on the completed channel as a second counted drop; Dropped
        // must stay exactly the writer's own 1.
        var sink = new ErrorLogSink();
        using var captureFactory = LoggerFactory.Create(b => b.AddProvider(new ErrorLogCaptureProvider(sink)));
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(conn).UseLoggerFactory(captureFactory).Options;
        await using (var db = new AuthDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ErrorLog;");
        }
        var writer = new ErrorLogWriter(sink, new ErrorLogStore(new FixedOptionsAuthDb(options)),
            NullLogger<ErrorLogWriter>.Instance);
        sink.TryPost(Event());
        sink.Channel.Writer.Complete();

        await writer.StartAsync(CancellationToken.None);
        await writer.ExecuteTask!;

        Assert.Equal(1, sink.Dropped);
        Assert.False(sink.Channel.Reader.TryRead(out _));
    }

    private sealed class FixedOptionsAuthDb(DbContextOptions<AuthDbContext> options) : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => new(options);
    }

    // ---------------------------------------------------------------------------------- writer

    [Fact]
    public async Task The_writer_drains_the_sink_into_the_store()
    {
        var sink = new ErrorLogSink();
        var writer = new ErrorLogWriter(sink, Store(), NullLogger<ErrorLogWriter>.Instance);
        sink.TryPost(Event());
        sink.TryPost(Event(template: "Another site failed", message: "Another site failed"));
        sink.Channel.Writer.Complete(); // deterministic end: the drain loop finishes the queue then exits

        await writer.StartAsync(CancellationToken.None);
        await writer.ExecuteTask!;

        Assert.Equal(2, (await Store().ListAsync()).Count);
    }

    [Fact]
    public async Task A_store_failure_becomes_a_counted_drop_and_the_writer_keeps_going()
    {
        var sink = new ErrorLogSink();
        var writer = new ErrorLogWriter(sink, new ErrorLogStore(new FailNextAuthDb(_authDb)),
            NullLogger<ErrorLogWriter>.Instance);
        sink.TryPost(Event()); // hits the injected failure
        sink.TryPost(Event(template: "Survivor", message: "Survivor"));
        sink.Channel.Writer.Complete();

        await writer.StartAsync(CancellationToken.None);
        await writer.ExecuteTask!;

        Assert.Equal(1, sink.Dropped);
        var row = Assert.Single(await Store().ListAsync());
        Assert.Equal("Survivor", row.LastMessage);
    }

    [Fact]
    public async Task Shutdown_drains_queued_events_instead_of_abandoning_them()
    {
        var sink = new ErrorLogSink();
        var writer = new ErrorLogWriter(sink, Store(), NullLogger<ErrorLogWriter>.Instance);
        await writer.StartAsync(CancellationToken.None);

        // Prove the loop is genuinely RUNNING before stopping: the runtime may start ExecuteAsync
        // through a cancellable hop, and under a saturated pool a stop can cancel the loop before
        // it ever ran — the first event landing is the aliveness signal that closes that race.
        sink.TryPost(Event());
        for (var waited = 0; (await Store().ListAsync()).Count == 0; waited += 50)
        {
            Assert.True(waited < 10_000, "the writer never started processing");
            await Task.Delay(50);
        }
        sink.TryPost(Event(template: "Another site failed", message: "Another site failed"));

        // If StopAsync ever stops completing the channel, the drain never ends and this hangs
        // rather than fails — the timeout converts that into a clean failure.
        await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, (await Store().ListAsync()).Count);
        Assert.Equal(0, sink.Dropped);
    }

    [Fact]
    public async Task A_stop_that_beat_the_loop_counts_what_it_could_not_drain()
    {
        // The never-ran edge itself (here forced by never starting): whatever a stop cannot drain
        // must land in the drop count — the one loss path that would otherwise be silent.
        var sink = new ErrorLogSink();
        var writer = new ErrorLogWriter(sink, Store(), NullLogger<ErrorLogWriter>.Instance);
        sink.TryPost(Event());
        sink.TryPost(Event(template: "Another site failed", message: "Another site failed"));

        await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, sink.Dropped);
        Assert.False(sink.Channel.Reader.TryRead(out _)); // swept, not stranded
    }

    /// <summary>Fails the first CreateDbContext, then delegates — the shape of a transient DB
    /// hiccup mid-drain.</summary>
    private sealed class FailNextAuthDb(TestAuthDb inner) : IDbContextFactory<AuthDbContext>
    {
        private bool _failed;

        public AuthDbContext CreateDbContext()
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException("transient failure");
            }
            return inner.CreateDbContext();
        }
    }
}
