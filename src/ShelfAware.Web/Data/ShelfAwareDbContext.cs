using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Data;

public class ShelfAwareDbContext(DbContextOptions<ShelfAwareDbContext> options) : DbContext(options)
{
    /// <summary>The household this context instance is scoped to. Set by <c>IHouseholdDbFactory</c> right
    /// after creation; every query is then filtered to that household and every insert stamped with it.
    /// Null = an UNSCOPED context (bootstrap, tests' escape hatch): queries see only ownerless rows and
    /// inserts are left alone — never a way to read another household's data.</summary>
    public string? HouseholdId { get; set; }

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
    public DbSet<AiUsage> AiUsages => Set<AiUsage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Recipe exposes computed domain helpers (IsVariant, and the MainIngredients/Seasonings splits of
        // its own Ingredients). They're behaviour, not stored state — tell EF not to treat them as columns
        // or as rival navigations to RecipeIngredient. (The method-form helpers aren't mapped by convention.)
        modelBuilder.Entity<Recipe>().Ignore(r => r.IsVariant);
        modelBuilder.Entity<Recipe>().Ignore(r => r.MainIngredients);
        modelBuilder.Entity<Recipe>().Ignore(r => r.Seasonings);

        // Multi-tenancy (v3): every tenant table filters on the context's HouseholdId. The filter lambda
        // captures THIS context instance, so EF parameterizes it per instance — the standard tenant
        // pattern. Children are filtered exactly like their parents, so Include paths never mix scopes.
        ApplyHousehold<Product>(modelBuilder);
        ApplyHousehold<PurchaseEvent>(modelBuilder);
        ApplyHousehold<Receipt>(modelBuilder);
        ApplyHousehold<ReceiptLine>(modelBuilder);
        ApplyHousehold<ProductAlias>(modelBuilder);
        ApplyHousehold<InventorySignal>(modelBuilder);
        ApplyHousehold<ProductTag>(modelBuilder);
        ApplyHousehold<ProductSubstitute>(modelBuilder);
        ApplyHousehold<ExcludedFood>(modelBuilder);
        ApplyHousehold<Recipe>(modelBuilder);
        ApplyHousehold<RecipeIngredient>(modelBuilder);
        ApplyHousehold<RecipeStep>(modelBuilder);
        ApplyHousehold<GroceryExtra>(modelBuilder);
        ApplyHousehold<AiUsage>(modelBuilder);

        // One usage row per household per day (the upsert's race-safety anchor).
        modelBuilder.Entity<AiUsage>()
            .HasIndex(u => new { u.HouseholdId, u.Day })
            .IsUnique();

        // AppSettings are per household too, keyed (HouseholdId, Key). HouseholdId is non-nullable
        // here (PK member; the CLR default "" only exists so an Added row has a stampable value).
        // For an UNSCOPED context EF folds this filter to FALSE (null compared to a non-nullable
        // column), so background code that forgot to pick a household reads no settings at all —
        // pinned by EfAppSettingsTests.
        modelBuilder.Entity<AppSetting>().HasKey(s => new { s.HouseholdId, s.Key });
        modelBuilder.Entity<AppSetting>().HasQueryFilter(s => s.HouseholdId == HouseholdId);

        // Aliases are learned per household (each teaches its own matcher), so uniqueness is too.
        modelBuilder.Entity<ProductAlias>()
            .HasIndex(a => new { a.HouseholdId, a.Merchant, a.RawText })
            .IsUnique();

        modelBuilder.Entity<PurchaseEvent>()
            .HasIndex(p => new { p.ProductId, p.PurchasedAt });
    }

    private void ApplyHousehold<TEntity>(ModelBuilder modelBuilder) where TEntity : class, IHouseholdOwned
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e => e.HouseholdId == HouseholdId);
        modelBuilder.Entity<TEntity>().HasIndex(e => e.HouseholdId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampHousehold();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampHousehold();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Stamps the current household onto every newly added row, so no service or page ever sets
    /// tenancy by hand. The IsNullOrEmpty check lets AppSetting's "" CLR default be overwritten while an
    /// explicitly pre-assigned id is respected.</summary>
    private void StampHousehold()
    {
        if (HouseholdId is null) return; // unscoped context — writes are left exactly as the caller made them
        foreach (var entry in ChangeTracker.Entries<IHouseholdOwned>())
        {
            if (entry.State == EntityState.Added && string.IsNullOrEmpty(entry.Entity.HouseholdId))
            {
                entry.Entity.HouseholdId = HouseholdId;
            }
        }
    }
}
