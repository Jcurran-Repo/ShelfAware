using Microsoft.EntityFrameworkCore;
using ShelfAware.Core.Census;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
using ShelfAware.Core.Ingest;
using ShelfAware.Core.Settings;
using ShelfAware.Web.Data;

namespace ShelfAware.Web.Ingest;

/// <summary>
/// The graduated-trust router for uploaded receipts: decides, per the household's
/// <see cref="ImportMode"/>, whether a freshly extracted PendingReview receipt is confirmed on the
/// spot or waits for human review. This is the folder importer's brain, kept when the folder
/// transport was retired (2026-07-22) — uploads are the one way receipts arrive now, so the
/// Smart/Auto behaviour rides them instead. Works off the STORED receipt (both upload paths persist
/// before review), so it and the review pre-fill read the same lines and can't disagree.
/// </summary>
public sealed class ReceiptAutoConfirmer(
    IHouseholdDbFactory dbFactory,
    IAppSettings settings,
    ReceiptConfirmationService confirmer,
    ReceiptDuplicateDetector duplicates,
    ILogger<ReceiptAutoConfirmer> logger)
{
    /// <summary>A line auto-confirms only at/above this extraction confidence (unless an alias vouches
    /// for it). Below it, Smart mode queues the receipt for human review.</summary>
    public const decimal SmartConfidenceFloor = 0.8m;

    /// <summary>What happened to the receipt: confirmed with the counts the UI reports, or left
    /// pending for review (all-zero counts). <paramref name="Duplicate"/> names the confirmed
    /// receipt this one looks like when THAT is why it queued.</summary>
    public sealed record Outcome(bool Confirmed, int Purchases, int NewProducts, int Retracked,
        ReceiptDuplicateDetector.Match? Duplicate = null,
        IReadOnlyList<ReceiptConfirmationService.QuantityConcern>? QuantityConcerns = null)
    {
        public static readonly Outcome Queued = new(false, 0, 0, 0);
    }

    /// <summary>The household's active mode — the Upload page shows it so an auto-confirm is never a
    /// surprise, and routing reads the same parse (legacy AutoConfirmImports still honored).</summary>
    public async Task<ImportMode> ModeAsync(CancellationToken cancellationToken = default) =>
        ImportModes.Parse(
            await settings.GetAsync(SettingKeys.ImportMode, cancellationToken),
            await settings.GetAsync(SettingKeys.AutoConfirmImports, cancellationToken));

    /// <summary>
    /// Confirm the pending receipt if the household's mode trusts it; otherwise leave it queued.
    /// Smart trusts a receipt only when EVERY line resolves to an already-known product via a learned
    /// alias or a confident match — and, stricter than the retired folder importer, only when the
    /// extraction found a purchase date: the date drives every prediction, and "no date, assume
    /// today" is exactly the silent guess review exists to catch. Auto keeps its all-or-nothing
    /// contract (undated confirms as today) with ONE exception shared by every mode: a receipt the
    /// <see cref="ReceiptDuplicateDetector"/> flags as an exact re-upload always queues — silently
    /// double-recording is the mistake this router exists to not automate.
    /// </summary>
    public async Task<Outcome> TryConfirmAsync(int receiptId, CancellationToken cancellationToken = default)
    {
        var mode = await ModeAsync(cancellationToken);
        if (mode == ImportMode.Review) return Outcome.Queued;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var receipt = await db.Receipts.AsNoTracking().Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);
        // Gone, already confirmed, or discarded — nothing for this router to decide.
        if (receipt is null || receipt.Status != ReceiptStatus.PendingReview) return Outcome.Queued;

        // Zero purchasable lines (e.g. not actually a receipt) always queues — confirming an empty
        // receipt would just hide it.
        if (receipt.Lines.Count == 0) return Outcome.Queued;

        if (mode == ImportMode.Smart && receipt.PurchasedAt is null)
        {
            logger.LogInformation("Queued receipt {ReceiptId} for review: no purchase date detected (Smart mode).",
                receipt.Id);
            return Outcome.Queued;
        }

        // A detected duplicate NEVER auto-confirms — not even in Confirm-everything mode. Silently
        // double-recording is the exact failure this router must not automate; recording a genuine
        // twin trip anyway is one human click on the review it queues into.
        if (await duplicates.FindDuplicateAsync(receipt.Id, cancellationToken) is { } dupe)
        {
            logger.LogInformation(
                "Queued receipt {ReceiptId} for review: looks like an exact duplicate of confirmed receipt {DuplicateId} ({Mode} mode).",
                receipt.Id, dupe.ReceiptId, mode);
            return Outcome.Queued with { Duplicate = dupe };
        }

        var merchant = receipt.Merchant ?? "";
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken);
        // Identity questions below (suggestion resolve, twin check) go through one index built per
        // receipt, not a full-catalog ExactMatches scan per line — same move the census grid made.
        var catalog = new CatalogIndex(products);
        var aliases = await db.ProductAliases.AsNoTracking()
            .Where(a => a.Merchant == merchant).ToListAsync(cancellationToken);

        // Resolve each stored line by the same trust order the review pre-fill uses:
        // learned alias → model suggestion → deterministic matcher → create new.
        var confirmLines = new List<ReceiptConfirmationService.ConfirmLine>();
        var allTrusted = true;
        // ⚠️ An ambiguous-twin line is not auto-confirmable in ANY mode — see below.
        var autoBlocked = false;
        foreach (var line in receipt.Lines)
        {
            var name = line.NormalizedName.Trim();
            if (name.Length == 0)
            {
                // A nameless line can't be confirmed. Smart puts a human on it; Auto drops it,
                // exactly as the folder importer always did under its all-or-nothing contract.
                allTrusted = false;
                continue;
            }

            var alias = aliases.FirstOrDefault(a => a.RawText == line.RawText);
            var resolved = alias is not null ? products.FirstOrDefault(p => p.Id == alias.ProductId) : null;
            // ⚠️ The model's suggestion names a NAME, and no unique index exists on product names — so a
            // FirstOrDefault(string.Equals) over TWINS silently picked one and committed a purchase to its
            // history. Resolve the suggestion by the matcher's rule-1 identity set instead (which also folds
            // a punctuation variant onto the one product it names): exactly one match uses it; twins fall
            // through unresolved and are caught by the ambiguity check below.
            resolved ??= line.SuggestedProduct is { Length: > 0 }
                    && catalog.ExactMatches(line.SuggestedProduct) is { Count: 1 } one
                ? one[0]
                : null;
            resolved ??= ProductMatcher.Resolve(name, products);

            // A NAME-based resolution that lands on a name TWO products share is a coin flip about which one
            // this line means — the matcher's own First()-over-twins. Attaching a purchase to an arbitrary
            // twin's history is exactly the machine judgement this router must not make unreviewed, so such a
            // line is NOT auto-confirmable in any mode — the same "break Auto for the one case it can't
            // decide" contract ReceiptDuplicateDetector holds for exact duplicates. It routes to review,
            // where a human picks the right twin.
            // ⚠️ Only when `alias is null`. A learned alias resolves by ProductId, so it names ONE product
            // outright even when that product's name is a twin — the human already taught which one, and
            // bouncing that certain pairing to review is a false alarm. (A suggestion resolves only at
            // ExactMatches.Count == 1, so the matcher fallback is the sole path that can land on a twin by
            // name.) The census refuses to guess between twins for the same reason: an attest, like a
            // purchase, lands on ONE product.
            var ambiguous = alias is null && resolved is not null && catalog.ExactMatches(resolved.Name).Count > 1;
            if (ambiguous) autoBlocked = true;

            // Trusted = a human-taught alias vouches for it, or it's a confident match to a product
            // that already exists. A brand-new product, a shaky line, or an ambiguous twin gets human eyes.
            allTrusted &= !ambiguous && (alias is not null || (resolved is not null && line.Confidence >= SmartConfidenceFloor));

            confirmLines.Add(new ReceiptConfirmationService.ConfirmLine(
                line.RawText, name, line.Brand, line.Size, line.Variety, line.Quantity, line.Category,
                ReceiptConfirmationService.DeserializeTags(line.TagsJson), ambiguous ? 0 : resolved?.Id ?? 0));
        }

        var confirm = confirmLines.Count > 0 && mode switch
        {
            ImportMode.Auto => !autoBlocked,
            ImportMode.Smart => allTrusted,
            _ => false,
        };

        if (!confirm)
        {
            logger.LogInformation("Queued receipt {ReceiptId} for review: {Lines} line(s) ({Mode} mode).",
                receipt.Id, receipt.Lines.Count, mode);
            return Outcome.Queued;
        }

        // writeAliases: false — no human looked at these pairings, so they must not become sticky
        // merchant aliases (a wrong machine match would silently pre-match every future receipt).
        // verifiedForEval stays false for the same reason: machine confirms are never ground truth.
        var purchaseDate = receipt.PurchasedAt ?? DateOnly.FromDateTime(DateTime.Today);
        var outcome = await confirmer.ConfirmAsync(receipt.Id, purchaseDate, confirmLines, writeAliases: false,
            cancellationToken: cancellationToken);
        logger.LogInformation("Auto-confirmed uploaded receipt {ReceiptId}: {Purchases} purchase(s), {NewProducts} new product(s) ({Mode} mode).",
            receipt.Id, outcome.Purchases, outcome.NewProducts, mode);
        // A pack-misread concern does NOT block the auto-confirm (a bulk buyer must not be stopped, and
        // one tap fixes it) — it rides out on the Outcome so the done-panel can surface it, and it's
        // persisted on the line for /receipts. This is deliberately unlike the duplicate case above,
        // which DOES block: a wrong quantity is recoverable in a tap; a silent double-record isn't.
        return new Outcome(true, outcome.Purchases, outcome.NewProducts, outcome.Retracked,
            QuantityConcerns: outcome.QuantityConcerns);
    }
}
