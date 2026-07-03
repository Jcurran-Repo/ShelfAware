using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Data;

public class ShelfAwareDbContext(DbContextOptions<ShelfAwareDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<PurchaseEvent> PurchaseEvents => Set<PurchaseEvent>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<ReceiptLine> ReceiptLines => Set<ReceiptLine>();
    public DbSet<ProductAlias> ProductAliases => Set<ProductAlias>();
    public DbSet<InventorySignal> InventorySignals => Set<InventorySignal>();
    public DbSet<ProductTag> ProductTags => Set<ProductTag>();
    public DbSet<ExcludedFood> ExcludedFoods => Set<ExcludedFood>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeIngredient> RecipeIngredients => Set<RecipeIngredient>();
    public DbSet<RecipeStep> RecipeSteps => Set<RecipeStep>();
    public DbSet<GroceryExtra> GroceryExtras => Set<GroceryExtra>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSetting>().HasKey(s => s.Key);

        modelBuilder.Entity<ProductAlias>()
            .HasIndex(a => new { a.Merchant, a.RawText })
            .IsUnique();

        modelBuilder.Entity<PurchaseEvent>()
            .HasIndex(p => new { p.ProductId, p.PurchasedAt });
    }
}
