namespace ShelfAware.Core.Domain;

/// <summary>One runtime-editable app setting (v2 Settings page). A tiny key/value store — single-user,
/// so no need for anything fancier. <see cref="Key"/> is the primary key.</summary>
public class AppSetting
{
    public required string Key { get; set; }
    public string Value { get; set; } = "";
}
