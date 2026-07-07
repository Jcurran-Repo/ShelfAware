namespace ShelfAware.Core.Domain;

/// <summary>Marks an entity as belonging to one household (the v3 tenancy unit — accounts live in the
/// separate auth DB and reference households by this plain id; there is no cross-database FK).
/// The Web DbContext filters every query by the current household and stamps the id on inserts, so
/// domain and service code never handles tenancy explicitly. Plain C# — Core stays EF-free.</summary>
public interface IHouseholdOwned
{
    string? HouseholdId { get; set; }
}
