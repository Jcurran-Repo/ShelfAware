using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Undo;

/// <summary>The names either side of a merge, for the history line. History-only, so nothing is reversed —
/// a merge folds purchases, aliases, signals, tags and recipe links into the target and deletes the source,
/// and rebuilding that split is not something v1 undoes.</summary>
public sealed record ProductsMergedPayload(string SourceName, string TargetName);

/// <summary>History-only record of a product merge (<c>ProductMergeService</c>): recorded, shown greyed on
/// /history, never undone.</summary>
public sealed class ProductsMergedHandler : HistoryOnlyHandler<ProductsMergedPayload>
{
    public override ActivityKind Kind => ActivityKind.ProductsMerged;

    protected override string Summarize(ProductsMergedPayload p) => $"Merged {p.SourceName} into {p.TargetName}";
}
