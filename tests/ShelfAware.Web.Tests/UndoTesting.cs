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
    private static readonly IReadOnlyList<IUndoHandler> Handlers = BuildHandlers();

    public static ActivityLogService Log(IHouseholdDbFactory db, int? maxRows = null) =>
        new(db, Handlers, Options.Create(new ActivityLogOptions { MaxRows = maxRows }),
            NullLogger<ActivityLogService>.Instance);

    private static IReadOnlyList<IUndoHandler> BuildHandlers()
    {
        var services = new ServiceCollection();
        UndoServiceCollectionExtensions.AddHandlers(services);
        return [.. services.BuildServiceProvider().GetServices<IUndoHandler>()];
    }
}
