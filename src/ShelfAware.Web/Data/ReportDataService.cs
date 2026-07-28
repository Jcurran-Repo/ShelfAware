using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Reporting;
using ShelfAware.Core.Shopping;

namespace ShelfAware.Web.Data;

/// <summary>One row of the gap report: how long one of something lasts versus how often it's rebought.
/// Both rhythms required — an item missing either has no gap to state.</summary>
public sealed record GapRow(int ProductId, string Name, double Burn, double Rebuy, double Gap);

/// <summary>One dated purchase and what the evidence says became of it (Waste watch).</summary>
public sealed record LabelJudgement(LabeledPurchase Purchase, LabelOutcome Outcome);

/// <summary>A recipe's main ingredients, with the product names they were matched to at save time.</summary>
public sealed record RecipeMains(int RecipeId, string Name, IReadOnlyList<RecipeIngredient> Mains);

/// <summary>Everything a report render needs, loaded once. Kept separate from the ReportFacts rows
/// so the builder UI can offer real choices (which products exist, which tags, how far back data
/// goes) without a second trip.</summary>
public sealed record ReportSourceData(
    IReadOnlyList<PurchaseFact> Purchases,
    IReadOnlyList<MealFact> Meals,
    IReadOnlyList<(int Id, string Name)> Products,
    IReadOnlyList<string> Tags,
    DateOnly? FirstPurchase,
    /// <summary>What one of each product costs NOW (the dominant size bucket's most recent paid
    /// price — the same "Current" the Trends ticker shows), keyed by product NAME because that's
    /// what RecipeIngredient.MatchedProduct stores. Case-insensitive.</summary>
    IReadOnlyDictionary<string, decimal> CurrentPriceByProductName);

/// <summary>
/// Joins the household's EF rows into the report engine's flat facts — the one place reporting
/// touches the database. Pricing mirrors the Trends page exactly: a purchase's own receipt-line
/// price is the paid truth, the size-aware index estimate fills spend gaps, and dominant-size
/// membership is stamped here (via the shared PriceSeries/SizeBucket) so the engine's UnitPrice
/// metric compares like with like without knowing what a "size" is.
/// <para><see cref="LoadAsync"/> is the shared fact load every preset reuses; the three Load*
/// methods below serve presets that need rows the facts don't carry (full purchase and signal
/// histories, expiration labels, recipe mains). They live here rather than in the page because the
/// page can't be reached by a test: the one real bug in this layer — a backlog row re-deriving a due
/// date instead of asking the engine — shipped past a fully green suite and was caught only by
/// noticing that two screens disagreed.</para>
/// </summary>
public sealed class ReportDataService(IHouseholdDbFactory dbFactory)
{
    public async Task<ReportSourceData> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var products = await db.Products.AsNoTracking()
            .Select(p => new { p.Id, p.Name, p.Category })
            .ToListAsync(ct);
        var productById = products.ToDictionary(p => p.Id);

        var tagsByProduct = (await db.ProductTags.AsNoTracking()
                .Select(t => new { t.ProductId, t.Value })
                .ToListAsync(ct))
            .GroupBy(t => t.ProductId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(t => t.Value).ToList());

        // The priced observations, receipt-dated — the same base rows the Trends page reads.
        var lineData = await db.ReceiptLines.AsNoTracking()
            .Where(l => l.ProductId != null && l.UnitPrice != null)
            .Select(l => new
            {
                ProductId = l.ProductId!.Value,
                l.ReceiptId,
                l.Size,
                Price = l.UnitPrice!.Value,
                Date = l.Receipt!.PurchasedAt,
            })
            .ToListAsync(ct);

        var priceIndex = new ProductPriceIndex(lineData.Select(x => (x.ProductId, x.Size, x.Price)));
        var byReceiptProduct = lineData
            .GroupBy(x => (x.ReceiptId, x.ProductId))
            .ToDictionary(g => g.Key, g => g.Average(x => x.Price));
        var dominantByProduct = lineData
            .GroupBy(x => x.ProductId)
            .ToDictionary(
                g => g.Key,
                g => PriceSeries.Dominant(g.Select(x => new PricePoint(x.Size, x.Date, x.Price)).ToList())!);

        var purchases = await db.PurchaseEvents.AsNoTracking().ToListAsync(ct);
        var purchaseFacts = new List<PurchaseFact>(purchases.Count);
        foreach (var pe in purchases)
        {
            if (!productById.TryGetValue(pe.ProductId, out var product)) continue;

            // The purchase's own receipt line is the exact paid price; the index is the estimate.
            decimal? paid = pe.ReceiptId is { } rid && byReceiptProduct.TryGetValue((rid, pe.ProductId), out var linePrice)
                ? linePrice : null;
            var inDominant = dominantByProduct.TryGetValue(pe.ProductId, out var dominant)
                && SizeBucket.Key(pe.Size) == dominant.SizeKey;

            purchaseFacts.Add(new PurchaseFact(
                pe.PurchasedAt,
                pe.ProductId,
                product.Name,
                product.Category,
                pe.Quantity,
                paid ?? priceIndex.PriceFor(pe.ProductId, pe.Size),
                paid,
                inDominant,
                tagsByProduct.GetValueOrDefault(pe.ProductId) ?? []));
        }

        var mealFacts = (await db.MealEvents.AsNoTracking()
                .Join(db.Recipes,
                    m => m.RecipeId, r => r.Id,
                    (m, r) => new { m.AteAt, m.RecipeId, r.Name, r.EstimatedCaloriesPerServing })
                .ToListAsync(ct))
            .Select(m => new MealFact(m.AteAt, m.RecipeId, m.Name, m.EstimatedCaloriesPerServing))
            .ToList();

        var currentPriceByName = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var (productId, series) in dominantByProduct)
        {
            if (productById.TryGetValue(productId, out var product) && series.Points.Count > 0)
                currentPriceByName[product.Name] = series.Points[^1].UnitPrice;
        }

        return new ReportSourceData(
            purchaseFacts,
            mealFacts,
            products.OrderBy(p => p.Name).Select(p => (p.Id, p.Name)).ToList(),
            tagsByProduct.Values.SelectMany(t => t).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
            purchaseFacts.Count > 0 ? purchaseFacts.Min(f => f.Date) : null,
            currentPriceByName);
    }

    /// <summary>The gap report: both learned rhythms per tracked item, widest gap first. An item with
    /// only one rhythm is dropped — a gap between a number and nothing isn't a number.</summary>
    public async Task<IReadOnlyList<GapRow>> LoadGapRowsAsync(
        DateOnly today, bool honorExpirations, CancellationToken ct = default)
    {
        var products = await LoadHistoriesAsync(ct);

        return products
            .Select(p => (Product: p, Prediction: ReplenishmentPredictor.Predict(p, today, honorExpirations)))
            .Where(x => x.Prediction is { RebuyIntervalDays: not null, BurnRateDays: not null })
            .Select(x => new GapRow(
                x.Product.Id,
                x.Product.Name,
                Burn: x.Prediction.BurnRateDays!.Value,
                Rebuy: x.Prediction.RebuyIntervalDays!.Value,
                Gap: x.Prediction.RebuyIntervalDays.Value - x.Prediction.BurnRateDays.Value))
            .OrderByDescending(x => x.Gap)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Waste watch: every dated purchase, judged from evidence by <see cref="ExpirationOutcomes"/>,
    /// newest label first. Priced from the facts (the same paid-then-estimate rule as spend).</summary>
    public async Task<IReadOnlyList<LabelJudgement>> LoadLabelOutcomesAsync(
        ReportSourceData source, DateOnly today, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var dated = await db.PurchaseEvents.AsNoTracking()
            .Where(p => p.ExpirationDate != null)
            .Select(p => new { p.ProductId, p.Product!.Name, p.PurchasedAt, Label = p.ExpirationDate!.Value })
            .ToListAsync(ct);
        if (dated.Count == 0) return [];

        var productIds = dated.Select(d => d.ProductId).Distinct().ToList();
        var purchaseDates = (await db.PurchaseEvents.AsNoTracking()
                .Where(p => productIds.Contains(p.ProductId))
                .Select(p => new { p.ProductId, p.PurchasedAt })
                .ToListAsync(ct))
            .GroupBy(p => p.ProductId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<DateOnly>)g.Select(p => p.PurchasedAt).ToList());
        var signals = (await db.InventorySignals.AsNoTracking()
                .Where(s => productIds.Contains(s.ProductId))
                .Select(s => new { s.ProductId, s.SignaledAt, s.Kind })
                .ToListAsync(ct))
            .GroupBy(s => s.ProductId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyCollection<(DateOnly, SignalKind)>)g
                    .Select(s => (SignalDate.Of(s.SignaledAt), s.Kind)).ToList());

        var priceByPurchase = source.Purchases
            .GroupBy(f => (f.ProductId, f.Date))
            .ToDictionary(g => g.Key, g => g.First().Price);

        return dated
            .Select(d =>
            {
                var purchase = new LabeledPurchase(d.ProductId, d.Name, d.PurchasedAt, d.Label,
                    priceByPurchase.GetValueOrDefault((d.ProductId, d.PurchasedAt)));
                return new LabelJudgement(purchase, ExpirationOutcomes.Judge(
                    purchase,
                    purchaseDates.GetValueOrDefault(d.ProductId) ?? [],
                    signals.GetValueOrDefault(d.ProductId) ?? [],
                    today));
            })
            .OrderByDescending(o => o.Purchase.Label)
            .ToList();
    }

    /// <summary>The backlog check (DESIGN.md §13.7). Assembles what <see cref="BacklogSignals"/> needs:
    /// full buy/outage histories, money from the facts, and — the load-bearing one — the ENGINE's own
    /// rhythm and due date, never a median recomputed here.</summary>
    public async Task<BacklogReport> LoadBacklogAsync(
        ReportSourceData source, DateOnly today, bool honorExpirations, int recentMealWindowDays,
        CancellationToken ct = default)
    {
        var products = await LoadHistoriesAsync(ct);
        var mealUses = RecentMealUsesByProductName(source, await LoadRecipeMainsAsync(ct), today, recentMealWindowDays);

        // Money comes from the report facts, priced exactly the way the Spend metric prices it
        // (ReportEngine.PurchaseValue): unit price × quantity, unpriced purchases counted not guessed.
        var moneyByProduct = source.Purchases
            .GroupBy(f => f.ProductId)
            .ToDictionary(
                g => g.Key,
                g => (Spend: g.Sum(f => (f.Price ?? 0) * f.Quantity),
                      Quantity: g.Sum(f => f.Quantity),
                      Unpriced: g.Count(f => f.Price is null)));

        return BacklogSignals.Find(
            products.Select(p =>
            {
                var money = moneyByProduct.GetValueOrDefault(p.Id);
                // ONE prediction, both numbers: the rhythm to show and the due date to test against.
                // It honours the household's expiration setting because the report's whole claim is
                // "the app says this is due" — so it must ask the question the dashboard asks. Running
                // expiration-blind here (an earlier version did) makes the page's own "the same number
                // the dashboard shows" footnote false for exactly the households that opted in.
                // Unlike the BACKTEST, which stays blind on purpose: that one grades the learned
                // rhythm, and a label is not a thing the rhythm predicted.
                var prediction = ReplenishmentPredictor.Predict(p, today, honorExpirations);
                return new BacklogInput(
                    p.Id,
                    p.Name,
                    p.Purchases.Select(x => x.PurchasedAt).ToList(),
                    // SignalDate.Of — the one reading, shared with the engine. The cycle pairing this
                    // feeds is the predictor's own, so a different reading of the same instant could
                    // pair a cycle here that the engine never sees.
                    p.Signals.Where(s => s.Kind == SignalKind.OutNow)
                        .Select(s => SignalDate.Of(s.SignaledAt)).ToList(),
                    money.Quantity,
                    money.Spend,
                    money.Unpriced,
                    prediction.RebuyIntervalDays,
                    prediction.DueDate,
                    mealUses.GetValueOrDefault(p.Name));
            }),
            today);
    }

    /// <summary>Saved recipes with their main ingredients — shared by the meal presets and the backlog
    /// check's "cooked with" column, so there's one definition of the query.</summary>
    public async Task<IReadOnlyList<RecipeMains>> LoadRecipeMainsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var recipes = await db.Recipes.AsNoTracking().Include(r => r.Ingredients).ToListAsync(ct);
        return recipes.Select(r => new RecipeMains(r.Id, r.Name, r.MainIngredients.ToList())).ToList();
    }

    /// <summary>Tracked products with the full histories the predictor needs. Untracked are excluded on
    /// purpose: untracking means "don't want it for a while", so such an item is quiet past its rhythm by
    /// construction and would re-nag about exactly what the household asked to stop hearing about.</summary>
    private async Task<List<Product>> LoadHistoriesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Products.AsNoTracking()
            .Where(p => p.IsTracked)
            .Include(p => p.Purchases)
            .Include(p => p.Signals)
            .ToListAsync(ct);
    }

    /// <summary>Meals in the recent window, counted per main-ingredient product NAME — the same key
    /// <c>RecipeIngredient.MatchedProduct</c> stores, so the join is a name lookup, not an id one. A
    /// recipe cooked twice counts twice for each of its mains, once per meal.</summary>
    private static Dictionary<string, int> RecentMealUsesByProductName(
        ReportSourceData source, IReadOnlyList<RecipeMains> recipeMains, DateOnly today, int windowDays)
    {
        var since = today.AddDays(-windowDays);
        var mainsByRecipe = recipeMains.ToDictionary(r => r.RecipeId, r => r.Mains);
        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var meal in source.Meals.Where(m => m.Date >= since))
        {
            if (!mainsByRecipe.TryGetValue(meal.RecipeId, out var mains)) continue;
            foreach (var name in mains
                .Select(m => m.MatchedProduct)
                .OfType<string>()
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                uses[name] = uses.GetValueOrDefault(name) + 1;
            }
        }

        return uses;
    }
}
