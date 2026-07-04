using ShelfAware.Core.Ingest;

namespace ShelfAware.Tests;

public class ImportModeTests
{
    [Theory]
    [InlineData("Review", null, ImportMode.Review)]
    [InlineData("smart", null, ImportMode.Smart)]     // case-insensitive
    [InlineData("Auto", "false", ImportMode.Auto)]     // explicit mode beats the legacy bool
    [InlineData(null, "true", ImportMode.Auto)]        // legacy auto-confirm on → Auto
    [InlineData(null, "false", ImportMode.Review)]     // legacy auto-confirm off → Review
    [InlineData(null, null, ImportMode.Smart)]         // unset → the graduated default
    [InlineData("nonsense", "nonsense", ImportMode.Smart)]
    public void Parse_honors_the_mode_then_the_legacy_bool_then_defaults_smart(
        string? mode, string? legacy, ImportMode expected) =>
        Assert.Equal(expected, ImportModes.Parse(mode, legacy));
}
