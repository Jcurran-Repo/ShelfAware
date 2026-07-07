namespace ShelfAware.Core.Domain;

/// <summary>One runtime-editable app setting (v2 Settings page). A tiny key/value store — per household
/// since v3: the primary key is (<see cref="HouseholdId"/>, <see cref="Key"/>).</summary>
public class AppSetting : IHouseholdOwned
{
    /// <summary>Non-nullable because it's part of the primary key (SQLite would silently accept a NULL
    /// TEXT PK — its famous quirk — so the CLR type forbids it outright). Stamped by the DbContext.</summary>
    public string HouseholdId { get; set; } = "";
    public required string Key { get; set; }
    public string Value { get; set; } = "";

    string? IHouseholdOwned.HouseholdId
    {
        get => HouseholdId;
        set => HouseholdId = value ?? "";
    }
}
