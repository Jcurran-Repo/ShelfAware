namespace ShelfAware.Core.Recipes;

/// <summary>How makeable a recipe is with what's on hand. <see cref="Ready"/> — every main ingredient is
/// food you own, so cook it as written. <see cref="NeedsSwap"/> — every main is covered, but at least one
/// only by a declared stand-in that may cook differently, so Adapt should rebuild the steps rather than
/// cook them as written ("also works as" means you'll eat it, not that it cooks the same). <see cref="Missing"/>
/// — at least one main is uncovered, or the recipe has no mains.</summary>
public enum Makeability { Missing, Ready, NeedsSwap }

/// <summary>The one definition of how a <see cref="Makeability"/> renders as a status chip — its CSS class
/// and its label — so the badge can never say "Ready to make" on the Recipes page and something else on the
/// Cookbook. Both surfaces (and the tests) read this single source rather than each keeping a copy; the same
/// one-definition discipline the app applies to prediction-status chips.</summary>
public static class MakeabilityFormat
{
    /// <summary>The status-chip CSS class: green (stocked) when Ready, amber (duesoon) when it needs a swap,
    /// grey (unknown) when something's missing.</summary>
    public static string ChipClass(Makeability makeability) => makeability switch
    {
        Makeability.Ready => "chip chip-stocked",
        Makeability.NeedsSwap => "chip chip-duesoon",
        _ => "chip chip-unknown",
    };

    /// <summary>The human label for the chip.</summary>
    public static string Label(Makeability makeability) => makeability switch
    {
        Makeability.Ready => "Ready to make",
        Makeability.NeedsSwap => "Makeable with a swap",
        _ => "Missing items",
    };
}
