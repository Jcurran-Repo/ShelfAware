namespace ShelfAware.Core.Domain;

/// <summary>Eggs's memory of one lookalike PAIR he flagged on the shopping list — two products that share a
/// food word nothing else does (see <c>SimilarPairs</c>). Household-owned. <see cref="FirstSeenAt"/> is when
/// he first flagged it, which drives how his mood degrades (<c>NudgeMoods</c>); <see cref="DismissedAt"/> is
/// null until you tell him "they're different", which is PERMANENT (he stops nagging about this pair) yet
/// reversible from either product's page. The pair is canonical — <see cref="LowerProductId"/> is always the
/// smaller id — so it has ONE identity however the two are ordered, matching the detector's canonical pair.
///
/// <para>The two product ids are plain breadcrumbs, NOT foreign keys: a product may be merged or deleted
/// while this row lingers harmlessly (the service only ever surfaces a pair whose two products are both
/// currently on the list, so a stale row is simply never shown — no cascade, no constraint to violate).</para></summary>
public class LookalikePair : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public int LowerProductId { get; set; }
    public int HigherProductId { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
}
