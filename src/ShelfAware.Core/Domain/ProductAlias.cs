namespace ShelfAware.Core.Domain;

public class ProductAlias
{
    public int Id { get; set; }
    public required string Merchant { get; set; }
    public required string RawText { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
}
