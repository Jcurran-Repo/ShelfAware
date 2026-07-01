namespace ShelfAware.Core.Domain;

/// <summary>
/// A food the user won't eat — allergy or dislike, one flat list (they don't distinguish: "if you hate
/// it you don't eat it"). Recipe suggestions hard-exclude these. Persistent.
/// </summary>
public class ExcludedFood
{
    public int Id { get; set; }
    public required string Value { get; set; }
}
