using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Undo;

/// <summary>How many products a shelf-photo census counted, and how many it introduced — for the history
/// line. History-only: a census attests counts and can create products and file OutNow signals across many
/// rows in one write, and unpicking that is not a v1 undo.</summary>
public sealed record CensusConfirmedPayload(int Counted, int NewProducts);

/// <summary>History-only record of a shelf-photo census (<c>CensusConfirmationService</c>): recorded, shown
/// greyed on /history, never undone.</summary>
public sealed class CensusConfirmedHandler : HistoryOnlyHandler<CensusConfirmedPayload>
{
    public override ActivityKind Kind => ActivityKind.CensusConfirmed;

    protected override string Summarize(CensusConfirmedPayload p) =>
        p.NewProducts > 0
            ? $"Counted {p.Counted} item{(p.Counted == 1 ? "" : "s")} from a photo ({p.NewProducts} new)"
            : $"Counted {p.Counted} item{(p.Counted == 1 ? "" : "s")} from a photo";
}
