using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Evaluation;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Services;

/// <summary>
/// The in-app extraction accuracy check: re-reads every receipt the user has VERIFIED (their
/// explicit "I checked every line" — see <see cref="Receipt.VerifiedForEval"/>) from its stored
/// audit copy and scores the fresh extraction against the confirmed lines with the SAME
/// <see cref="ExtractionScorer"/> the offline harness uses. On-demand only — each receipt costs a
/// full vision call, so nothing runs on page load. The last run is kept per household in
/// AppSettings so navigating away doesn't waste the spend. Scoped: rides the circuit's key
/// (BYOK visitors grade on their own wallet; managed mode is metered like any other call).
/// </summary>
public sealed class ReceiptSelfEval(
    IHouseholdDbFactory dbFactory,
    IReceiptExtractor extractor,
    CircuitAiSettings aiSettings,
    IAppSettings appSettings,
    ReceiptStorage storage,
    ILogger<ReceiptSelfEval> logger)
{
    public const string ResultsKey = "SelfEvalResults";

    public sealed record RunProgress(int Done, int Total, string Current);

    /// <summary>Verified receipts eligible for grading (confirmed + flag set), newest first.</summary>
    public async Task<int> CountVerifiedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Receipts.CountAsync(
            r => r.Status == ReceiptStatus.Confirmed && r.VerifiedForEval, cancellationToken);
    }

    public async Task<EvalResults?> GetLastRunAsync(CancellationToken cancellationToken = default)
    {
        var json = await appSettings.GetAsync(ResultsKey, cancellationToken);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<EvalResults>(json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Stored self-eval results were unreadable; treating as no run.");
            return null;
        }
    }

    public async Task<EvalResults> RunAsync(
        IProgress<RunProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        List<Receipt> verified;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            verified = await db.Receipts
                .AsNoTracking()
                .Where(r => r.Status == ReceiptStatus.Confirmed && r.VerifiedForEval)
                .Include(r => r.Lines)
                .ToListAsync(cancellationToken);
        }
        verified = [.. verified.OrderByDescending(r => r.PurchasedAt ?? DateOnly.MinValue)];

        var scores = new List<FixtureScore>();
        var done = 0;
        foreach (var receipt in verified)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = DisplayName(receipt);
            progress?.Report(new RunProgress(done, verified.Count, name));

            scores.Add(await GradeAsync(receipt, name, cancellationToken));
            done++;
        }
        progress?.Report(new RunProgress(done, verified.Count, ""));

        var results = new EvalResults
        {
            GeneratedAt = DateTimeOffset.Now,
            Model = aiSettings.ExtractionModel,
            Aggregate = ExtractionScorer.Aggregate(scores),
            Fixtures = scores,
        };
        await appSettings.SetAsync(ResultsKey, JsonSerializer.Serialize(results), cancellationToken);
        return results;
    }

    private async Task<FixtureScore> GradeAsync(Receipt receipt, string name, CancellationToken cancellationToken)
    {
        var expected = receipt.Lines.Select(l => new ExpectedLine
        {
            NormalizedName = l.NormalizedName,
            Quantity = l.Quantity,
            Category = l.Category.ToString(),
        }).ToList();

        var files = storage.Pages(receipt.ImagePath);
        if (files.Count == 0)
        {
            return new FixtureScore
            {
                Name = name, ExpectedLines = expected.Count,
                Error = "saved image missing — re-upload or unverify this receipt",
            };
        }

        try
        {
            var attachments = new List<ReceiptAttachment>();
            foreach (var file in files)
            {
                var (bytes, mediaType) = await storage.ReadPageAsync(file, cancellationToken);
                attachments.Add(new ReceiptAttachment(bytes, mediaType));
            }

            // No candidate-product list, matching the offline harness: this grades pure extraction,
            // not the product matcher.
            var result = await extractor.ExtractAsync(attachments, cancellationToken: cancellationToken);
            if (!result.Success || result.Receipt is null)
            {
                return new FixtureScore { Name = name, ExpectedLines = expected.Count, Error = result.Error };
            }

            var detail = ExtractionScorer.Score(expected, result.Receipt.Lines);
            return ExtractionScorer.ToFixtureScore(name, expected.Count, result.Receipt.Lines.Count, detail);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // One unreadable receipt must not sink the whole run — grade the rest, report this one.
            logger.LogError(ex, "Self-eval grading failed for receipt {ReceiptId}.", receipt.Id);
            return new FixtureScore { Name = name, ExpectedLines = expected.Count, Error = "grading failed — see logs" };
        }
    }

    private static string DisplayName(Receipt r)
    {
        var merchant = string.IsNullOrWhiteSpace(r.Merchant) ? "receipt" : r.Merchant;
        return r.PurchasedAt is { } d ? $"{merchant} {d:yyyy-MM-dd}" : $"{merchant} #{r.Id}";
    }

}
