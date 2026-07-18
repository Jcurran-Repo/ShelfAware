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

    /// <summary>Create a product with optional descriptive tags (canonicalized against the global
    /// vocabulary, same dedup as receipt confirmation). Pass an empty list for no tags.</summary>
    Task<int> CreateProductAsync(string name, Category category, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);

    /// <summary>Add descriptive tags to an existing product (canonicalized + deduped like receipt
    /// confirmation); returns the tag values actually added.</summary>
    Task<IReadOnlyList<string>> AddTagsAsync(int productId, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);

    /// <summary>The live tag vocabulary (seed ∪ every stored tag) — fed to the assistant so it reuses
    /// existing tags instead of coining near-duplicates (same dedup-at-source idea as extraction).</summary>
    Task<IReadOnlyList<string>> GetKnownTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Record a purchase. Returns true when it re-tracked an untracked product — buying an
    /// item again ends its "don't want it for a while" (the grocery list's Untrack), same as receipt
    /// confirmation — so the assistant can tell the user tracking resumed.</summary>
    Task<bool> AddPurchaseAsync(int productId, DateOnly purchasedAt, decimal quantity, CancellationToken cancellationToken = default);

    Task RecordSignalAsync(int productId, SignalKind kind, CancellationToken cancellationToken = default);

    /// <summary>Set (or clear, with null) the expiration date on a product's LATEST purchase — the only
    /// purchase whose date can mark the item out (rebuying supersedes the old jug). Returns false when
    /// the product has no purchases to carry a date.</summary>
    Task<bool> SetExpirationAsync(int productId, DateOnly? expiresOn, CancellationToken cancellationToken = default);

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
