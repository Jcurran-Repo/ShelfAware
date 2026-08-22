using HotChocolate;
using HotChocolate.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Prediction;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;
using ShelfAware.Web.GraphQL;

namespace ShelfAware.Web.Tests;

/// <summary>The computed fields (prediction/estimate via the pure engine) and the tags/substitutes
/// DataLoaders. The headline is "one prediction, one story": the API runs the engine with the SAME flags
/// the product surfaces use (honorQuantity: true, honorExpirations = the household setting), and the
/// prediction the estimate embeds is the same object as the prediction field beside it.</summary>
public class GraphQLComputedFieldsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>An overdue ~10-day-rhythm item the household has ALSO counted fresh (3 on hand, counted
    /// yesterday) — the shape where honorQuantity: true suppresses the buy (Stocked) but false leaves it
    /// Overdue. Also carries tags + a substitute for the DataLoaders. Mirrors ReplenishmentPredictorTests'
    /// CountedAndOverdue.</summary>
    private async Task<int> SeedSuppressingCountProduct(string household)
    {
        _db.HouseholdId = household;
        await using var db = _db.CreateDbContext();
        var product = new Product
        {
            Name = "Canned Beans",
            Category = Category.Pantry,
            TrackQuantity = true,
            QuantityOnHand = 3,
            QuantityCountedAt = DateTimeOffset.Now.AddDays(-1),
            Purchases =
            [
                new PurchaseEvent { PurchasedAt = Today.AddDays(-45), Quantity = 1 },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-35), Quantity = 1 },
                new PurchaseEvent { PurchasedAt = Today.AddDays(-25), Quantity = 1 },
            ],
            Tags = [new ProductTag { Value = "Canned" }, new ProductTag { Value = "Staple" }],
            Substitutes = [new ProductSubstitute { Value = "black beans" }],
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task<Product> LoadAsResolverDoes(int id)
    {
        await using var db = _db.CreateDbContext();
        return await db.Products.AsNoTracking()
            .Include(p => p.Purchases)
            .Include(p => p.Signals)
            .SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task PredictAsync_uses_the_product_surface_flags_exactly()
    {
        var id = await SeedSuppressingCountProduct("hh-a");
        _db.HouseholdId = "hh-a";
        var read = new PantryReadContext(_db, new FakeAppSettings()); // expiration tracking OFF
        var product = await LoadAsResolverDoes(id);

        var apiPrediction = await read.PredictAsync(product);

        // Exact record equality with the product-surface call — same today, honorExpirations from the
        // (off) setting, honorQuantity: true. A different flag would change this product's outcome.
        var expected = ReplenishmentPredictor.Predict(product, read.Today, honorExpirations: false, honorQuantity: true);
        Assert.Equal(expected, apiPrediction);

        // …and the flag actually bites here: honorQuantity: true suppressed the buy (Stocked); false would
        // leave it Overdue. So this equality genuinely pins honorQuantity: true, not just "some prediction".
        Assert.True(apiPrediction.SuppressedByCount);
        Assert.Equal(PredictionStatus.Stocked, apiPrediction.Status);
        Assert.Equal(PredictionStatus.Overdue,
            ReplenishmentPredictor.Predict(product, read.Today, honorExpirations: false, honorQuantity: false).Status);
    }

    [Fact]
    public async Task PredictAsync_honors_the_expiration_setting_both_ways()
    {
        // A single purchase whose label has already passed: honorExpirations: true marks it Expired; false
        // leaves it alone. So the setting is the discriminator.
        _db.HouseholdId = "hh-a";
        await using (var db = _db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Milk", Category = Category.Dairy,
                Purchases = [new PurchaseEvent { PurchasedAt = Today.AddDays(-5), Quantity = 1, ExpirationDate = Today.AddDays(-2) }],
            });
            await db.SaveChangesAsync();
        }
        var product = await LoadAsResolverDoes((await FirstProductId()));

        var settingsOn = new FakeAppSettings();
        await settingsOn.SetAsync(SettingKeys.TrackExpirationDates, "true");
        var on = new PantryReadContext(_db, settingsOn);
        var off = new PantryReadContext(_db, new FakeAppSettings());

        var predOn = await on.PredictAsync(product);
        var predOff = await off.PredictAsync(product);

        Assert.Equal(ReplenishmentPredictor.Predict(product, on.Today, honorExpirations: true, honorQuantity: true), predOn);
        Assert.Equal(ReplenishmentPredictor.Predict(product, off.Today, honorExpirations: false, honorQuantity: true), predOff);
        Assert.True(predOn.Expired);   // the label fired only with tracking on
        Assert.False(predOff.Expired);
    }

    [Fact]
    public async Task PredictAsync_memoizes_so_prediction_and_estimate_share_one_result()
    {
        var id = await SeedSuppressingCountProduct("hh-a");
        _db.HouseholdId = "hh-a";
        var read = new PantryReadContext(_db, new FakeAppSettings());
        var product = await LoadAsResolverDoes(id);

        var first = await read.PredictAsync(product);
        var second = await read.PredictAsync(product);

        Assert.Same(first, second); // one object — the prediction field and the estimate can't disagree
    }

    [Fact]
    public async Task The_computed_fields_and_dataloaders_resolve_end_to_end()
    {
        await SeedSuppressingCountProduct("hh-a");
        _db.HouseholdId = "hh-a";

        var json = await ExecuteAsync(
            "{ products { name prediction { status suppressedByCount } estimate { nextBuyDate } tags { value } substitutes { value } } }");

        Assert.DoesNotContain("errors", json);
        Assert.Contains("STOCKED", json);        // honorQuantity: true suppressed it
        Assert.Contains("Canned", json);         // tag, via TagsByProductDataLoader
        Assert.Contains("black beans", json);    // substitute, via SubstitutesByProductDataLoader
    }

    [Fact]
    public async Task The_prediction_reflects_signals_not_just_purchases()
    {
        // An active OutNow pins the item Overdue — but ONLY if the resolver loaded Signals (Query.cs
        // Includes them). Without that Include the signal is invisible and the prediction silently
        // contradicts the UI.
        _db.HouseholdId = "hh-a";
        await using (var db = _db.CreateDbContext())
        {
            db.Products.Add(new Product
            {
                Name = "Eggs", Category = Category.Dairy,
                Signals = [new InventorySignal { Kind = SignalKind.OutNow, SignaledAt = DateTimeOffset.Now.AddDays(-1) }],
            });
            await db.SaveChangesAsync();
        }

        var json = await ExecuteAsync("{ products { name prediction { status pinned } } }");

        Assert.DoesNotContain("errors", json);
        Assert.Contains("OVERDUE", json);
        Assert.Contains("\"pinned\":true", json.Replace(" ", ""));
    }

    private async Task<string> ExecuteAsync(string query)
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IHouseholdDbFactory>(_db)
            .AddSingleton<IAppSettings>(new FakeAppSettings())
            .AddPantryGraphQL()
            .Services
            .BuildServiceProvider();
        var executor = await provider.GetRequestExecutorAsync();
        var result = await executor.ExecuteAsync(query);
        return result.ToJson();
    }

    private async Task<int> FirstProductId()
    {
        await using var db = _db.CreateDbContext();
        return await db.Products.Select(p => p.Id).FirstAsync();
    }
}
