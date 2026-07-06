using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Chat;

/// <summary>
/// Data port the chat tools act through. Defined in Core (no EF here) and implemented in
/// Web so <see cref="IPantryChat"/> can read products and apply changes without referencing
/// the DbContext directly (DESIGN.md §3: Core has no EF).
/// </summary>
public interface IPantryStore
{
    /// <summary>All products with their purchases and signals loaded (needed for fuzzy
    /// matching and for running the prediction engine on a status query).</summary>
    Task<IReadOnlyList<Product>> GetProductsAsync(CancellationToken cancellationToken = default);

    Task<int> CreateProductAsync(string name, Category category, CancellationToken cancellationToken = default);

    Task AddPurchaseAsync(int productId, DateOnly purchasedAt, decimal quantity, CancellationToken cancellationToken = default);

    Task RecordSignalAsync(int productId, SignalKind kind, CancellationToken cancellationToken = default);

    /// <summary>Start or stop tracking a product for replenishment (untracked = kept in the catalog
    /// but not predicted or shown as running low).</summary>
    Task SetTrackingAsync(int productId, bool tracked, CancellationToken cancellationToken = default);

    /// <summary>Saved recipes (id, name, whether cooking steps exist) — lets the read_recipe chat
    /// tool resolve a spoken name to something the Recipes page can read aloud.</summary>
    Task<IReadOnlyList<RecipeRef>> GetRecipesAsync(CancellationToken cancellationToken = default);

    /// <summary>Add "also works as" substitutes to a product (deduped against what it already has);
    /// returns the values actually added. Lets the assistant fill in a product's substitutes by voice/chat.</summary>
    Task<IReadOnlyList<string>> AddSubstitutesAsync(int productId, IReadOnlyList<string> values, CancellationToken cancellationToken = default);

    /// <summary>Foods the user won't eat (allergies/dislikes) — passed to the recipe advisor so a
    /// generated recipe never includes them.</summary>
    Task<IReadOnlyList<string>> GetExcludedFoodsAsync(CancellationToken cancellationToken = default);

    /// <summary>Add items to the grocery list's manual "extras", skipping any already on it (case-
    /// insensitive); returns the names actually added. Deliberately NEVER records an inventory signal — a
    /// shopping-list add is not an "I'm out" statement, so it must not feed the prediction engine.</summary>
    Task<IReadOnlyList<string>> AddGroceryExtrasAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default);
}

/// <summary>Lightweight saved-recipe reference for chat-tool resolution.</summary>
public record RecipeRef(int Id, string Name, bool HasSteps);
