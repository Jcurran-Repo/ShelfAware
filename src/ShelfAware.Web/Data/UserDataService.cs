using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;

namespace ShelfAware.Web.Data;

/// <summary>
/// The one place that reads out or wipes everything the user has accumulated — backing both the
/// "Download my data" export and the "Delete my data" danger action. Kept as a service (not inline in
/// the page) so the delete order lives in exactly one tested spot and the export endpoint can reuse it.
///
/// Scope: the user's own content (pantry, purchase history, signals, receipts, recipes, lists). It does
/// NOT touch <c>AppSettings</c> (app configuration) or the visitor's BYOK keys — those are held in the
/// browser and cleared separately by "Forget my key".
/// </summary>
public sealed class UserDataService(IDbContextFactory<ShelfAwareDbContext> dbFactory)
{
    /// <summary>Everything, flattened (loaded without navigations so there are no serialization cycles) —
    /// a portable JSON snapshot the user can keep before deleting, or just as a backup.</summary>
    public async Task<DataExport> ExportAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return new DataExport
        {
            ExportedAt = DateTimeOffset.Now,
            Products = await db.Products.AsNoTracking().ToListAsync(ct),
            Purchases = await db.PurchaseEvents.AsNoTracking().ToListAsync(ct),
            Signals = await db.InventorySignals.AsNoTracking().ToListAsync(ct),
            Tags = await db.ProductTags.AsNoTracking().ToListAsync(ct),
            Substitutes = await db.ProductSubstitutes.AsNoTracking().ToListAsync(ct),
            Aliases = await db.ProductAliases.AsNoTracking().ToListAsync(ct),
            Receipts = await db.Receipts.AsNoTracking().ToListAsync(ct),
            ReceiptLines = await db.ReceiptLines.AsNoTracking().ToListAsync(ct),
            Recipes = await db.Recipes.AsNoTracking().ToListAsync(ct),
            RecipeIngredients = await db.RecipeIngredients.AsNoTracking().ToListAsync(ct),
            RecipeSteps = await db.RecipeSteps.AsNoTracking().ToListAsync(ct),
            ExcludedFoods = await db.ExcludedFoods.AsNoTracking().ToListAsync(ct),
            GroceryExtras = await db.GroceryExtras.AsNoTracking().ToListAsync(ct),
        };
    }

    /// <summary>Total rows the delete would remove — shown in the confirm dialog so the warning is
    /// concrete ("this removes 214 records") rather than vague.</summary>
    public async Task<int> CountAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Products.CountAsync(ct)
            + await db.PurchaseEvents.CountAsync(ct)
            + await db.InventorySignals.CountAsync(ct)
            + await db.ProductTags.CountAsync(ct)
            + await db.ProductSubstitutes.CountAsync(ct)
            + await db.ProductAliases.CountAsync(ct)
            + await db.Receipts.CountAsync(ct)
            + await db.ReceiptLines.CountAsync(ct)
            + await db.Recipes.CountAsync(ct)
            + await db.RecipeIngredients.CountAsync(ct)
            + await db.RecipeSteps.CountAsync(ct)
            + await db.ExcludedFoods.CountAsync(ct)
            + await db.GroceryExtras.CountAsync(ct);
    }

    /// <summary>Wipe every user-content table. In one transaction so it's all-or-nothing, and children
    /// before parents — ExecuteDelete is raw SQL, so it doesn't cascade and SQLite's FK enforcement would
    /// otherwise reject deleting a parent (e.g. a Product) while its rows still reference it.</summary>
    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.RecipeSteps.ExecuteDeleteAsync(ct);
        await db.RecipeIngredients.ExecuteDeleteAsync(ct);
        await db.Recipes.ExecuteDeleteAsync(ct);
        await db.ReceiptLines.ExecuteDeleteAsync(ct);
        await db.PurchaseEvents.ExecuteDeleteAsync(ct);   // references both Product and Receipt
        await db.InventorySignals.ExecuteDeleteAsync(ct);
        await db.ProductTags.ExecuteDeleteAsync(ct);
        await db.ProductSubstitutes.ExecuteDeleteAsync(ct);
        await db.ProductAliases.ExecuteDeleteAsync(ct);
        await db.Receipts.ExecuteDeleteAsync(ct);
        await db.Products.ExecuteDeleteAsync(ct);
        await db.ExcludedFoods.ExecuteDeleteAsync(ct);
        await db.GroceryExtras.ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
    }
}

/// <summary>A flat snapshot of all user content, for the JSON export/backup.</summary>
public sealed class DataExport
{
    public DateTimeOffset ExportedAt { get; init; }
    public IReadOnlyList<Product> Products { get; init; } = [];
    public IReadOnlyList<PurchaseEvent> Purchases { get; init; } = [];
    public IReadOnlyList<InventorySignal> Signals { get; init; } = [];
    public IReadOnlyList<ProductTag> Tags { get; init; } = [];
    public IReadOnlyList<ProductSubstitute> Substitutes { get; init; } = [];
    public IReadOnlyList<ProductAlias> Aliases { get; init; } = [];
    public IReadOnlyList<Receipt> Receipts { get; init; } = [];
    public IReadOnlyList<ReceiptLine> ReceiptLines { get; init; } = [];
    public IReadOnlyList<Recipe> Recipes { get; init; } = [];
    public IReadOnlyList<RecipeIngredient> RecipeIngredients { get; init; } = [];
    public IReadOnlyList<RecipeStep> RecipeSteps { get; init; } = [];
    public IReadOnlyList<ExcludedFood> ExcludedFoods { get; init; } = [];
    public IReadOnlyList<GroceryExtra> GroceryExtras { get; init; } = [];
}
