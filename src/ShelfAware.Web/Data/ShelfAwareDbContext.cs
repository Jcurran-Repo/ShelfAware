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
    public DbSet<ProductSubstitute> ProductSubstitutes => Set<ProductSubstitute>();
    public DbSet<ExcludedFood> ExcludedFoods => Set<ExcludedFood>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeIngredient> RecipeIngredients => Set<RecipeIngredient>();
    public DbSet<RecipeStep> RecipeSteps => Set<RecipeStep>();
    public DbSet<GroceryExtra> GroceryExtras => Set<GroceryExtra>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Recipe exposes computed domain helpers (IsVariant, and the MainIngredients/Seasonings splits of
        // its own Ingredients). They're behaviour, not stored state — tell EF not to treat them as columns
        // or as rival navigations to RecipeIngredient. (The method-form helpers aren't mapped by convention.)
        modelBuilder.Entity<Recipe>().Ignore(r => r.IsVariant);
        modelBuilder.Entity<Recipe>().Ignore(r => r.MainIngredients);
        modelBuilder.Entity<Recipe>().Ignore(r => r.Seasonings);

        modelBuilder.Entity<AppSetting>().HasKey(s => s.Key);

        modelBuilder.Entity<ProductAlias>()
            .HasIndex(a => new { a.Merchant, a.RawText })
            .IsUnique();

        modelBuilder.Entity<PurchaseEvent>()
            .HasIndex(p => new { p.ProductId, p.PurchasedAt });
    }
}
