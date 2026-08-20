using ShelfAware.Core.Domain;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Undo;

/// <summary>Reverse-and-check data for a logged meal ("Ate it"): which <see cref="MealEvent"/>, the recipe
/// name for the summary, and the ACTUAL packages the cook took off the shelf — auto-takes plus any the
/// picker resolved (the page restates this as picks land, so it stays equal to what happened). The takes
/// carry the CLAMPED amount, not the nominal package, so putting them back never invents stock.</summary>
public sealed record MealLoggedPayload(int MealEventId, string RecipeName, IReadOnlyList<MealStock.Applied> Taken);

/// <summary>Undo for a logged meal: remove the meal event, step the recipe's <c>TimesEaten</c> back, and
/// return the taken packages — all through <see cref="MealStock.ReverseMealOnAsync"/>, the SAME reversal
/// the inline "↩ Undo" runs (so the two surfaces can never disagree about what undoing a meal does).
/// <see cref="UndoResult.Gone"/> when the meal is no longer on record — another surface already reversed
/// it, so there is nothing to do.</summary>
public sealed class MealLoggedHandler : UndoHandler<MealLoggedPayload>
{
    public override ActivityKind Kind => ActivityKind.MealLogged;

    protected override string Summarize(MealLoggedPayload p) => $"Logged a meal: {p.RecipeName}";

    protected override async Task<UndoResult> Reverse(
        ShelfAwareDbContext db, MealLoggedPayload p, ActivityEntry entry, CancellationToken ct) =>
        await MealStock.ReverseMealOnAsync(db, p.MealEventId, p.Taken, ct)
            ? UndoResult.Done
            : UndoResult.Gone;
}
