namespace ShelfAware.Core.Domain;

/// <summary>Eggs's memory of one lookalike CLUSTER he flagged on the shopping list — three or more products
/// whose names all end in the same food word, two of which read as one product (see <c>SimilarCluster</c>:
/// five "… Dog Treats" holding the two Dentastix). Household-owned.
/// <see cref="FirstSeenAt"/> is when he first flagged it, which drives how his mood degrades (<c>NudgeMoods</c>);
/// <see cref="DismissedAt"/> is null until you tell him "they're all different", which is PERMANENT (he stops
/// nagging about this cluster) yet reversible from any member product's page.
///
/// <para>Keyed on the HEAD WORD (<see cref="Head"/>, the singular folded form the detector reports —
/// "yogurt", "treat"), NOT on the member products: a cluster's membership changes as products come and go,
/// and "they're all different" for the yogurts means don't ask about the yogurts again — predictable, and one
/// row per cluster rather than a row per pair inside it (a 40-product "sauce" cluster would be 780 of those).
/// A stale row (no cluster with that head on the list any more) lingers harmlessly: the service only ever
/// surfaces a cluster the detector currently reports, so a stale row is simply never shown.</para></summary>
public class LookalikeCluster : IHouseholdOwned
{
    public int Id { get; set; }
    public string? HouseholdId { get; set; }
    public string Head { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
}
