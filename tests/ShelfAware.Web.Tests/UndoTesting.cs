using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Web.Data;
using ShelfAware.Web.Undo;

namespace ShelfAware.Web.Tests;

/// <summary>Builds a REAL <see cref="ActivityLogService"/> over a <see cref="TestDb"/> with the same undo
/// handlers production wires — resolved from <c>AddHandlers</c> itself, so the test set can never drift
/// from the DI set. Never a no-op stand-in: a fake recorder more permissive than the real one is exactly
/// the trap items 20/33 keep catching, so recording and undo are exercised end-to-end.</summary>
internal static class UndoTesting
{
    private static readonly IReadOnlyList<IUndoHandler> Handlers = BuildHandlers(new NoOpImageCleanup());

    /// <summary>The default log — receipt-image deletion is a no-op, which every test that isn't ABOUT the
    /// image cleanup wants (the DB reversal is what they assert). Use <see cref="Log(IHouseholdDbFactory,
    /// IReceiptImageCleanup, int?)"/> with a <see cref="RecordingImageCleanup"/> to pin the cleanup itself.</summary>
    public static ActivityLogService Log(IHouseholdDbFactory db, int? maxRows = null) =>
        new(db, Handlers, Options.Create(new ActivityLogOptions { MaxRows = maxRows }),
            NullLogger<ActivityLogService>.Instance);

    /// <summary>A log whose handlers delete receipt images through <paramref name="imageCleanup"/> — a
    /// <see cref="RecordingImageCleanup"/> pins that a real undo asks to delete the right folder, and that
    /// a Peek never does.</summary>
    public static ActivityLogService Log(IHouseholdDbFactory db, IReceiptImageCleanup imageCleanup, int? maxRows = null) =>
        new(db, BuildHandlers(imageCleanup), Options.Create(new ActivityLogOptions { MaxRows = maxRows }),
            NullLogger<ActivityLogService>.Instance);

    private static IReadOnlyList<IUndoHandler> BuildHandlers(IReceiptImageCleanup imageCleanup)
    {
        var services = new ServiceCollection();
        services.AddSingleton(imageCleanup);
        UndoServiceCollectionExtensions.AddHandlers(services);
        return [.. services.BuildServiceProvider().GetServices<IUndoHandler>()];
    }

    private sealed class NoOpImageCleanup : IReceiptImageCleanup
    {
        public void DeleteFolder(string imagePath) { }
    }
}

/// <summary>Records the receipt-image folders an undo asks to forget, so a test can assert the RIGHT one
/// was deleted (and, crucially, that a Peek deleted NONE — a Peek re-runs the reversal to grey the row and
/// must never touch the filesystem).</summary>
internal sealed class RecordingImageCleanup : IReceiptImageCleanup
{
    public List<string> Deleted { get; } = [];
    public void DeleteFolder(string imagePath) => Deleted.Add(imagePath);
}
