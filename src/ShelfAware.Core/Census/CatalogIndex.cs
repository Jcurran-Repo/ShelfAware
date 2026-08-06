using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;

namespace ShelfAware.Core.Census;

/// <summary>
/// The existing product catalog, indexed once for a census pass. Built from a household's products at load
/// time and asked the identity questions <see cref="CensusPlan"/> needs — "which products carry this name?",
/// "which product has this id?" — without re-normalizing the whole catalog on every call.
/// <para>It replaces both the O(N²) twin scan the grid ran per read (an <c>ExactMatches</c> for every product,
/// each normalizing every name) and the three per-render <c>ExactMatches</c> memo dictionaries, with one
/// <c>identityKey → products</c> map built once. "Which product does this name mean?" is answered by
/// <see cref="ProductMatcher.IdentityKey"/> here exactly as it is everywhere else in the app.</para>
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
        return key.Length > 0 && _byIdentity.TryGetValue(key, out var list) ? list : [];
    }

    /// <summary>The fuzzy resolver's best match and which rule fired — for a typed name that resembles but does
    /// not identity-match anything. Delegates to <see cref="ProductMatcher.ResolveWithKind"/> (the IDF weighting
    /// is inherently over the whole catalog); called only for create-candidate rows, not every row.</summary>
    public (Product? Product, ProductMatcher.MatchKind Kind) ResolveWithKind(string? name) =>
        ProductMatcher.ResolveWithKind(name, Products);
}
