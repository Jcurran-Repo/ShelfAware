using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Census;

/// <summary>
/// The existing product catalog, indexed once for a pass over many names — a census read and its review
/// grid, or a receipt's lines. Built from a household's products at load time and asked the identity
/// questions <see cref="CensusPlan"/> and the receipt pre-fill need — "which products carry this name?",
/// "which product has this id?" — without re-normalizing the whole catalog on every call.
/// <para>It replaces both the O(N²) twin scan the grid ran per read (an <c>ExactMatches</c> for every product,
/// each normalizing every name) and the three per-render <c>ExactMatches</c> memo dictionaries, with one
/// <c>identityKey → products</c> map built once. "Which product does this name mean?" is answered by
/// <see cref="ProductMatcher.IdentityKey"/> here exactly as it is everywhere else in the app.</para>
/// <para>⚠️ The index treats its catalog as IMMUTABLE for its own lifetime — that is what makes the
/// resolve memo below sound. Build a fresh index after any change to the products it was built from.</para>
/// </summary>
public sealed class CatalogIndex
{
    private readonly Dictionary<string, List<Product>> _byIdentity;
    private readonly Dictionary<int, Product> _byId;

    /// <summary>The products this index was built from, in the order given — the fuzzy matcher needs the full
    /// list (its IDF weighting is over the whole catalog).</summary>
    public IReadOnlyList<Product> Products { get; }

    public CatalogIndex(IReadOnlyList<Product> products)
    {
        Products = products;
        _byId = [];
        _byIdentity = [];
        foreach (var p in products)
        {
            _byId[p.Id] = p;
            var key = ProductMatcher.IdentityKey(p.Name);
            // This `continue` is the ONE guard that keeps a punctuation-only name (empty identity key) out
            // of the index — without it, every such junk name would collide under "" and merge. ExactMatches
            // no longer double-guards (it relies on this skip), so the mutant that drops this `continue` is
            // killable, and A_punctuation_only_name_is_never_indexed_or_matched kills it. No annotation.
            if (key.Length == 0) continue;
            if (!_byIdentity.TryGetValue(key, out var list)) _byIdentity[key] = list = [];
            list.Add(p);
        }
    }

    /// <summary>The product with this id, or null. Census-created products (id 0) are never here — the index
    /// holds existing products only.</summary>
    public Product? ById(int id) => _byId.GetValueOrDefault(id);

    /// <summary>Every product rule 1 (<see cref="ProductMatcher.IdentityKey"/>) calls an identity for this
    /// name — the full set, because a name two products share is the whole reason a census may not attest over
    /// "the" exact match without asking. Same answer as <see cref="ProductMatcher.ExactMatches"/>, precomputed.</summary>
    public IReadOnlyList<Product> ExactMatches(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var key = ProductMatcher.IdentityKey(name);
        // No `key.Length > 0` guard needed: the ctor never indexes an empty key, so a punctuation-only
        // name (key "") simply misses here — TryGetValue("") is false — and returns []. Leaving the guard
        // out keeps the "distinct junk names don't merge" guarantee (proven by A_punctuation_only_name…)
        // while sparing a redundant relational operator whose only non-equivalent mutant it would need an
        // annotation to suppress.
        return _byIdentity.TryGetValue(key, out var list) ? list : [];
    }

    private readonly Dictionary<string, (Product? Product, ProductMatcher.MatchKind Kind)> _resolveMemo = [];

    /// <summary>The fuzzy resolver's best match and which rule fired — for a typed name that resembles but does
    /// not identity-match anything. Delegates to <see cref="ProductMatcher.ResolveWithKind"/> (the IDF weighting
    /// is inherently over the whole catalog), memoized per query: the catalog is immutable for the index's
    /// lifetime, so a resolve is a pure function of the name — and without the memo the census grid paid a full
    /// catalog re-normalization plus an IDF rebuild per create-candidate row on EVERY render (the grid's "why"
    /// message then asked the same question again), for answers that cannot change between keystrokes.</summary>
    public (Product? Product, ProductMatcher.MatchKind Kind) ResolveWithKind(string? name)
    {
        // Stryker disable once String: the "" fallback is only a memo CACHE KEY for a null name — the
        // resolved result (ProductMatcher.ResolveWithKind(null, …)) is the same whatever key null caches
        // under, and no real product name collides with it.
        var key = name ?? "";
        if (!_resolveMemo.TryGetValue(key, out var hit))
            _resolveMemo[key] = hit = ProductMatcher.ResolveWithKind(name, Products);
        return hit;
    }
}
