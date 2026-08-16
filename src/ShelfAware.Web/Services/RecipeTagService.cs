using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Recipes;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>
/// The one path that writes a recipe's tags — AI suggestions (the cookbook's "Suggest tags" action and,
/// later, import) and manual add/remove. Every write canonicalizes through
/// <see cref="RecipeTagVocabulary"/> against the household's live vocabulary, so no two callers drift on
/// dedup policy. AI suggestion fails open (an untagged recipe is a cosmetic gap, never a blocked action);
/// manual edits surface their result to the caller.
/// </summary>
public class RecipeTagService
{
    private readonly IHouseholdDbFactory _dbFactory;
    private readonly IRecipeTagAdvisor _advisor;
    private readonly ILogger<RecipeTagService> _logger;

    public RecipeTagService(IHouseholdDbFactory dbFactory, IRecipeTagAdvisor advisor, ILogger<RecipeTagService> logger)
    {
        _dbFactory = dbFactory;
        _advisor = advisor;
        _logger = logger;
    }

    /// <summary>Ask the advisor for tags and apply the ones that are genuinely new. Best-effort: the
    /// advisor fails open, and any other failure is logged and swallowed (cancellation propagates), so a
    /// caller can fire this without guarding around it. Returns the tags actually added (empty on a no-op,
    /// a miss, or a failure).</summary>
    public async Task<IReadOnlyList<string>> SuggestAndApplyAsync(int recipeId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            if (await LoadAsync(db, recipeId, ct) is not { } recipe) return [];

            var vocabulary = await VocabularyAsync(db, ct);
            var before = recipe.Tags.Select(t => t.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var suggested = await _advisor.SuggestAsync(
                recipe.Name, recipe.Ingredients.Select(i => i.Name).ToList(), vocabulary, ct);
            if (suggested.Count == 0) return [];

            RecipeTagVocabulary.ApplyTags(recipe, suggested, vocabulary);
            await db.SaveChangesAsync(ct);
            return recipe.Tags.Select(t => t.Value).Where(v => !before.Contains(v)).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Suggesting tags for recipe {RecipeId} failed; leaving it as-is.", recipeId);
            return [];
        }
    }

    /// <summary>Manually add one tag, canonicalized + deduped against the household vocabulary. Returns the
    /// stored (canonical) tag, or null when it was blank or already present (as itself or a near-duplicate).</summary>
    public async Task<string?> AddAsync(int recipeId, string tag, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (await LoadAsync(db, recipeId, ct) is not { } recipe) return null;

        var vocabulary = await VocabularyAsync(db, ct);
        var before = recipe.Tags.Count;
        RecipeTagVocabulary.ApplyTags(recipe, [tag], vocabulary);
        if (recipe.Tags.Count == before) return null; // blank, or already carried it / a near-duplicate
        await db.SaveChangesAsync(ct);
        return recipe.Tags[^1].Value;
    }

    /// <summary>Remove a tag by value (case-insensitive). No-op if the recipe doesn't carry it. The compare
    /// is in memory (OrdinalIgnoreCase), not a SQL LOWER — SQLite string comparison is case-sensitive.</summary>
    public async Task RemoveAsync(int recipeId, string tag, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var recipe = await db.Recipes.Include(r => r.Tags).FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        var row = recipe?.Tags.FirstOrDefault(t => string.Equals(t.Value, tag, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        db.RecipeTags.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The household's tag vocabulary: the seed list plus every distinct recipe tag already in
    /// use (sorted), so suggestions and manual-add both lean on what's there. THE definition the service
    /// and the cookbook's manual-add datalist read.</summary>
    public static async Task<List<string>> VocabularyAsync(ShelfAwareDbContext db, CancellationToken ct = default)
    {
        var stored = await db.RecipeTags.Select(t => t.Value).Distinct().ToListAsync(ct);
        return RecipeTagVocabulary.Seed.Concat(stored)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static Task<Recipe?> LoadAsync(ShelfAwareDbContext db, int recipeId, CancellationToken ct) =>
        db.Recipes.Include(r => r.Ingredients).Include(r => r.Tags).FirstOrDefaultAsync(r => r.Id == recipeId, ct);
}
