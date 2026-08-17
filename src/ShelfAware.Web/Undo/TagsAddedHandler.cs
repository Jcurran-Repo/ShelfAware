using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for added product tags: the product and the exact tag values this
/// action added (canonicalized, so they match the stored rows) — never ones already present.</summary>
public sealed record TagsAddedPayload(int ProductId, string ProductName, IReadOnlyList<string> Values);

/// <summary>Undo for added tags: remove exactly the tags this action added that still exist (a tag
/// removed by hand since simply isn't there to remove). <see cref="UndoResult.Gone"/> when none remain —
/// there is nothing left to undo.</summary>
public sealed class TagsAddedHandler : UndoHandler<TagsAddedPayload>
{
    public override ActivityKind Kind => ActivityKind.TagsAdded;

    protected override string Summarize(TagsAddedPayload p) =>
        $"Tagged {p.ProductName}: {string.Join(", ", p.Values)}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, TagsAddedPayload p, ActivityEntry entry, CancellationToken ct)
    {
        var tags = await db.ProductTags
            .Where(t => t.ProductId == p.ProductId && p.Values.Contains(t.Value))
            .ToListAsync(ct);
        if (tags.Count == 0) return UndoResult.Gone; // all already removed — nothing to undo
        db.ProductTags.RemoveRange(tags);
        return UndoResult.Done;
    }
}
