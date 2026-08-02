using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Census;

/// <summary>
/// How the reader knows what an item is — the one field that keeps a shelf photo honest.
/// <para>A receipt is text: <c>raw_text</c> is either there or it isn't, so extraction never has to say
/// how sure it is of the item's IDENTITY separately from its normalization. A photo has no such floor. A
/// freezer looks like a freezer, and a model asked "what's in here?" can produce a plausible pantry out of
/// nothing but priors — peas, corn, chicken — every word of which is invented. Grading the evidence is what
/// stops that being indistinguishable from reading the labels: a claim that came off printed text and a
/// claim that came off a silhouette are different KINDS of claim, and the grid says which is which rather
/// than blending both into one confidence number.</para>
/// </summary>
public enum CensusEvidence
{
    /// <summary>The package says so. <see cref="CensusItem.LabelText"/> carries the text that was legible —
    /// the census's answer to <c>raw_text</c>, and the thing a human can check against the photo in a
    /// second. If it says FISH on the box, it's fish.</summary>
    Label,

    /// <summary>No legible label; identified by appearance alone. Not automatically a guess — a bunch of
    /// bananas carries no text and needs none — but not verifiable from the photo either, which is why
    /// confidence has to carry the weight here.</summary>
    Appearance,

    /// <summary>Something is on the shelf and the reader could not say what it is. The honest answer for a
    /// foil parcel or a box facing the wrong way, and deliberately its own value rather than a low
    /// confidence on an invented name: "I think this might be tilapia" and "I can see a package and have
    /// no idea" are different findings, and only one of them is asking the human to check a guess. Here
    /// <see cref="CensusItem.NormalizedName"/> describes the PACKAGE, not the food.</summary>
    Unidentified,
}

/// <summary>
/// One candidate item read off a shelf photo (DESIGN.md §13.8). Mirrors the receipt <c>ExtractedLine</c>
/// shape where the two genuinely align — a name, a brand, a category, a confidence — and diverges where a
/// shelf is not a receipt: no price, no purchase date, and a count of what is VISIBLE rather than a
/// quantity that was bought.
/// </summary>
public record CensusItem
{
    /// <summary>What was legible on the package, verbatim — the census's <c>raw_text</c>. Null when nothing
    /// could be read, which is exactly when <see cref="Evidence"/> stops being <see cref="CensusEvidence.Label"/>.
    /// It exists so a human reviewing thirty rows can check a claim against the photo without re-deriving it.</summary>
    public string? LabelText { get; init; }

    /// <summary>How the reader arrived at this item. See <see cref="CensusEvidence"/> — this is the field
    /// that separates reading from guessing.</summary>
    public required CensusEvidence Evidence { get; init; }

    /// <summary>The canonical item name, on the same brand-stripped rules as receipt extraction so a census
    /// and a receipt name the same food the same way (and therefore roll up into one product).
    /// For <see cref="CensusEvidence.Unidentified"/> this describes the package instead ("foil-wrapped parcel").</summary>
    public required string NormalizedName { get; init; }

    public string? Brand { get; init; }
    public string? Size { get; init; }
    /// <summary>Flavor/varietal, per the standing per-purchase-metadata rule (v3.5). Carried so a census row
    /// reads like a receipt line; a census attests a COUNT, so nothing downstream stores it.</summary>
    public string? Variety { get; init; }
    public Category Category { get; init; } = Category.Other;

    /// <summary>How many of this item are VISIBLE in the photos — never an estimate of what the shelf
    /// holds. §13.8's honest limits are occlusion and stacking: the back row and the cans behind the front
    /// can cannot be seen, so a number that tried to account for them would be invented. The photo proposes
    /// the front row; the human corrects it, which is the whole review step.
    /// <para><b>An int, deliberately.</b> You cannot see 2.34 of something. A human editing the row can type
    /// a fraction (the count itself is decimal — §13.1, weight items are fractional), but the reader is not
    /// allowed to produce one, because a fractional count off a photo could only ever be a guess at a weight
    /// the picture does not show.</para></summary>
    public int VisibleCount { get; init; } = 1;

    /// <summary>Certainty in the IDENTIFICATION, 0–1. Below 0.6 the review grid shows the row but leaves it
    /// unticked — the same threshold the receipt grid highlights a low-confidence line at, kept identical so
    /// there is one number in the app for "the model was guessing".</summary>
    public decimal Confidence { get; init; }

    /// <summary>Exact name of an existing product the reader judged this to be, or null. Only set when a
    /// candidate product list is passed in. A census is the app's bulk product creator, so this matters more
    /// here than anywhere: a twin product splits purchase history and blinds the predictor.</summary>
    public string? SuggestedProductName { get; init; }
}
