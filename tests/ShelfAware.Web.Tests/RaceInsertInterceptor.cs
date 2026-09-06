using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ShelfAware.Web.Tests;

/// <summary>Fires once, just before a save reaches the DB, running the supplied action — which inserts
/// today's row from a separate context, so the intercepted save then hits the unique index. Lets both
/// meters' upsert collision-fallback (the concurrent-first-insert race) be exercised deterministically,
/// without spawning real threads.</summary>
internal sealed class RaceInsertInterceptor(Func<CancellationToken, Task> insertColliding) : SaveChangesInterceptor
{
    private bool _fired;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!_fired)
        {
            _fired = true;
            await insertColliding(cancellationToken);
        }
        return result;
    }
}
