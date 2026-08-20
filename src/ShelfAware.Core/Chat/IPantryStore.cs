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

    /// <summary>ONE product by id, purchases and signals loaded, or null when no such product exists in
    /// this household. For a handler that must re-read a single product's CURRENT state — the same-day-tie
    /// caveat re-reads after a write earlier in the same turn — where re-loading the whole catalog was the
    /// price that made the re-read tempting to skip.</summary>
    Task<Product?> GetProductAsync(int productId, CancellationToken cancellationToken = default);

    /// <summary>Create a product with optional descriptive tags (canonicalized against the global
    /// vocabulary, same dedup as receipt confirmation) and an optional display <paramref name="defaultUnit"/>.
    /// Pass an empty tag list for no tags. THE one create path — records a ProductCreated activity entry —
    /// so a product added from any surface (chat, the Products page) is logged and undoable alike.</summary>
    Task<int> CreateProductAsync(
        string name, Category category, IReadOnlyList<string> tags, string? defaultUnit = null,
        CancellationToken cancellationToken = default);

    /// <summary>Add descriptive tags to an existing product (canonicalized + deduped like receipt
    /// confirmation); returns the tag values actually added.</summary>
    Task<IReadOnlyList<string>> AddTagsAsync(int productId, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);

    /// <summary>The live tag vocabulary (seed ∪ every stored tag) — fed to the assistant so it reuses
    /// existing tags instead of coining near-duplicates (same dedup-at-source idea as extraction).</summary>
    Task<IReadOnlyList<string>> GetKnownTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Record a purchase. Returns whether it re-tracked an untracked product (buying an item
    /// again ends its "don't want it for a while" — the grocery list's Untrack — so the assistant can
    /// tell the user tracking resumed) plus a reference to the undo entry it recorded, for an inline
    /// "↩ Undo". <paramref name="source"/> records the surface it came from (the dashboard passes Manual;
    /// chat defaults to Chat).</summary>
    Task<PurchaseResult> AddPurchaseAsync(int productId, DateOnly purchasedAt, decimal quantity, PurchaseSource source = PurchaseSource.Chat, CancellationToken cancellationToken = default);

    /// <summary>Record an inventory signal (restocked / out / running-low). Returns a reference to the
    /// undo entry it recorded (for an inline "↩ Undo"), or null when the product isn't in this household.</summary>
    Task<ActivityRef?> RecordSignalAsync(int productId, SignalKind kind, CancellationToken cancellationToken = default);

    /// <summary>Set (or clear, with null) the expiration date on a product's LATEST purchase — the only
    /// purchase whose date can mark the item out (rebuying supersedes the old jug). Returns false when
    /// the product has no purchases to carry a date.</summary>
    Task<bool> SetExpirationAsync(int productId, DateOnly? expiresOn, CancellationToken cancellationToken = default);

    /// <summary>Correct a recorded purchase's quantity (§13.6) — a misread receipt line, typically. THE
    /// write path for it, because the correction has to move the on-hand count by the DIFFERENCE too:
    /// fixing the history and leaving the shelf wrong would just relocate the error. Refuses a
    /// non-positive quantity rather than clamping — this is a number a person typed on purpose, and
    /// silently changing it is how the app would start disagreeing with them.</summary>
    Task<bool> SetPurchaseQuantityAsync(
        int purchaseId, decimal quantity, CancellationToken cancellationToken = default);

    /// <summary>Correct a recorded purchase's brand — a misread or missing receipt brand. Unlike the
    /// quantity correction this moves NO count and fires nothing: a product is brand-agnostic and the
    /// cadence pools across every brand, so the brand is cosmetic — it only feeds the "usual brand"
    /// hint and the Brands-bought breakdown. A null or blank value clears it back to unbranded. The
    /// receipt's own line is left alone (the audit copy of what was read), same as the quantity
    /// correction. Returns false only when the purchase isn't found (in this household).</summary>
    Task<bool> SetPurchaseBrandAsync(
        int purchaseId, string? brand, CancellationToken cancellationToken = default);

    /// <summary>THE write path for a human's count (§13.3). Shared by the product page, the one-tap
    /// adjusters and the <c>set_quantity</c> tool, because §13.4's asserted-zero rule has to hold on
    /// every road in: a person saying "none left" writes a real OutNow, and only a person's does.
    /// <paramref name="relative"/> adds a delta ("used two" → -2) instead of setting a total — a delta
    /// moves the number but does NOT re-anchor the attestation clock (§13.1: only a look at the shelf
    /// does; landing at zero is the exception). <paramref name="stopCounting"/> stops believing the
    /// count; the number and its date stay, dormant (§13.3).</summary>
    Task<bool> SetQuantityAsync(
        int productId, decimal quantity, bool relative = false, bool stopCounting = false,
        CancellationToken cancellationToken = default);

    /// <summary>Set (or clear, with null/blank) the product's display unit — the label
    /// <c>QuantityFormat.Describe</c> appends ("2.34 lb" instead of a bare "2.34"). Display ONLY: §13.3's
    /// decrement reads the fractionality of the quantities, never this field. Exists because the manual
    /// add-a-product form used to be the field's only writer, so a receipt-imported weight item could
    /// never say "lb" no matter what the household typed anywhere.</summary>
    Task<bool> SetDefaultUnitAsync(int productId, string? unit, CancellationToken cancellationToken = default);

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

/// <summary>The id and stored summary of a recorded undoable action — enough for an inline "↩ Undo".</summary>
public sealed record ActivityRef(int Id, string Summary);

/// <summary>Recording a purchase: whether it re-tracked the product, and the undo entry it recorded
/// (null only when the product wasn't found in this household).</summary>
public sealed record PurchaseResult(bool Retracked, ActivityRef? Recorded);
