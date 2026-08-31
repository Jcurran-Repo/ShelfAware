using ShelfAware.Core.Recipes;

namespace ShelfAware.Tests;

// The one definition of the makeability badge (chip class + label), shared by the Recipes page and the
// Cookbook so they can't drift. Each arm is distinct, so a mutated switch fails exactly its row.
public class MakeabilityFormatTests
{
    [Theory]
    [InlineData(Makeability.Ready, "chip chip-stocked", "Ready to make")]
    [InlineData(Makeability.NeedsSwap, "chip chip-duesoon", "Makeable with a swap")]
    [InlineData(Makeability.Missing, "chip chip-unknown", "Missing items")]
    public void Each_makeability_maps_to_one_chip_class_and_label(Makeability makeability, string chipClass, string label)
    {
        Assert.Equal(chipClass, MakeabilityFormat.ChipClass(makeability));
        Assert.Equal(label, MakeabilityFormat.Label(makeability));
    }
}
