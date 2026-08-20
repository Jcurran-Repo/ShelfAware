using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for added "also works as" substitutes: the product and the exact
/// values this action added (never ones already present).</summary>
public sealed record SubstitutesAddedPayload(int ProductId, string ProductName, IReadOnlyList<string> Values);

/// <summary>Undo for added substitutes: remove exactly the ones this action added that still exist.
/// <see cref="UndoResult.Gone"/> when none remain.</summary>
public sealed class SubstitutesAddedHandler : UndoHandler<SubstitutesAddedPayload>
{
    public override ActivityKind Kind => ActivityKind.SubstitutesAdded;

    protected override string Summarize(SubstitutesAddedPayload p) =>
        $"Added substitutes for {p.ProductName}: {string.Join(", ", p.Values)}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, SubstitutesAddedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var subs = await db.ProductSubstitutes
            .Where(s => s.ProductId == p.ProductId && p.Values.Contains(s.Value))
            .ToListAsync(ct);
        if (subs.Count == 0) return UndoResult.Gone;
        db.ProductSubstitutes.RemoveRange(subs);
        return UndoResult.Done;
    }
}
