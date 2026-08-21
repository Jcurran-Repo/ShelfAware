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
    private static readonly IReadOnlyList<IUndoHandler> Handlers =
        BuildHandlers(new NoOpReceiptImageCleanup(), new NoOpRecipeImageCleanup());

    /// <summary>The default log — the two image-cleanup seams are no-ops, which every test that isn't ABOUT
    /// image cleanup wants (the DB reversal is what they assert). Use the cleanup overloads with a recording
    /// fake to pin the cleanup itself.</summary>
    public static ActivityLogService Log(IHouseholdDbFactory db, int? maxRows = null) =>
        new(db, Handlers, Options.Create(new ActivityLogOptions { MaxRows = maxRows }),
            NullLogger<ActivityLogService>.Instance);

    /// <summary>A log whose handlers delete RECEIPT images through <paramref name="imageCleanup"/>.</summary>
    public static ActivityLogService Log(IHouseholdDbFactory db, IReceiptImageCleanup imageCleanup, int? maxRows = null) =>
        new(db, BuildHandlers(imageCleanup, new NoOpRecipeImageCleanup()),
            Options.Create(new ActivityLogOptions { MaxRows = maxRows }), NullLogger<ActivityLogService>.Instance);

    /// <summary>A log whose handlers reap RECIPE photos through <paramref name="imageCleanup"/> — a
    /// <see cref="RecordingRecipeImageCleanup"/> pins that a real recipe undo reaps the right file, and that a
    /// Peek never does.</summary>
    public static ActivityLogService Log(IHouseholdDbFactory db, IRecipeImageCleanup imageCleanup, int? maxRows = null) =>
        new(db, BuildHandlers(new NoOpReceiptImageCleanup(), imageCleanup),
            Options.Create(new ActivityLogOptions { MaxRows = maxRows }), NullLogger<ActivityLogService>.Instance);

    private static IReadOnlyList<IUndoHandler> BuildHandlers(
        IReceiptImageCleanup receiptCleanup, IRecipeImageCleanup recipeCleanup)
    {
        var services = new ServiceCollection();
        services.AddSingleton(receiptCleanup);
        services.AddSingleton(recipeCleanup);
        UndoServiceCollectionExtensions.AddHandlers(services);
        return [.. services.BuildServiceProvider().GetServices<IUndoHandler>()];
    }

    private sealed class NoOpReceiptImageCleanup : IReceiptImageCleanup
    {
        public void DeleteFolder(string imagePath) { }
    }

    private sealed class NoOpRecipeImageCleanup : IRecipeImageCleanup
    {
        public void Delete(string imagePath) { }
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

/// <summary>Records the recipe-photo files a recipe undo reaps — same purpose as
/// <see cref="RecordingImageCleanup"/>, for the recipe side.</summary>
internal sealed class RecordingRecipeImageCleanup : IRecipeImageCleanup
{
    public List<string> Deleted { get; } = [];
    public void Delete(string imagePath) => Deleted.Add(imagePath);
}
