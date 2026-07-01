namespace ShelfAware.Core.Domain;

/// <summary>
/// A one-off item on the grocery list that isn't a tracked product — added manually or pulled from a
/// recipe's missing ingredients. The prediction-driven list covers staples; this covers everything else.
/// </summary>
public class GroceryExtra
{
    public int Id { get; set; }
    public required string Name { get; set; }
}
