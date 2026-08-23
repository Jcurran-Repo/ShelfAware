using HotChocolate.Types;
using ShelfAware.Core.Prediction;

namespace ShelfAware.Web.GraphQL;

/// <summary>The GraphQL shape of a <see cref="PredictionResult"/> — the pure engine's replenishment
/// answer for a product, computed on read (never stored). Explicitly bound to a curated set: the
/// buying/rhythm facts a client would want, minus the internal-UI-only bits (ProductId is redundant with
/// the parent Product; SignalTodayWouldBeInert is a surface-copy concern).</summary>
public sealed class PredictionType : ObjectType<PredictionResult>
{
    protected override void Configure(IObjectTypeDescriptor<PredictionResult> descriptor)
    {
        descriptor.Name("Prediction");
        descriptor.BindFieldsExplicitly();
        descriptor.Field(p => p.Status);
        descriptor.Field(p => p.DueDate);
        descriptor.Field(p => p.MedianIntervalDays);
        descriptor.Field(p => p.RebuyIntervalDays);
        descriptor.Field(p => p.BurnRateDays);
        descriptor.Field(p => p.IntervalSpreadDays);
        descriptor.Field(p => p.StockUpFactor);
        descriptor.Field(p => p.RecommendedSize);
        descriptor.Field(p => p.Basis);
        descriptor.Field(p => p.SignalNote);
        descriptor.Field(p => p.Pinned);
        descriptor.Field(p => p.ExpiresOn);
        descriptor.Field(p => p.Expired);
        descriptor.Field(p => p.ExpirationOverridden);
        descriptor.Field(p => p.DueCappedByExpiration);
        descriptor.Field(p => p.SuppressedByCount);
        descriptor.Field(p => p.CountConfidence);
        descriptor.Field(p => p.CountLooksStale);
        descriptor.Field(p => p.CountRunsOutOn);
        descriptor.Field(p => p.RebuyBurnGapDays);
        descriptor.Field(p => p.RunsOutEarly);
    }
}
