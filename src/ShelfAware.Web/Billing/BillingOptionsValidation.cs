using Microsoft.Extensions.Options;
using ShelfAware.Core.Billing;

namespace ShelfAware.Web.Billing;

/// <summary>
/// What a <see cref="BillingOptions"/> has to satisfy for this box to start. A named, pure function rather
/// than three lambdas buried in <c>Program.cs</c>, so the rules can be tested — a validator nothing exercises
/// is a validator that gets deleted in a refactor without anyone noticing it was load-bearing.
///
/// <para>⚠️ The two anchor rules exist because <see cref="Data.CreditDenominationMigration"/> is
/// IRREVERSIBLE. It divides every pre-credit balance by the retail price of a credit, once, on the first
/// boot that sees the old schema. <see cref="CreditPricing.RetailMicrosPerCredit"/> clamps that divisor to 1
/// micro rather than returning zero — right everywhere that arithmetic has to stay defined, and catastrophic
/// there — so a $1.65 welcome grant would convert to 1,650,000 credits with no second boot to put it back.
/// A box that cannot price a credit must not start.</para>
/// </summary>
public static class BillingOptionsValidation
{
    /// <summary>The first rule this <paramref name="options"/> breaks, in words an operator can act on, or
    /// null when it is sound. <paramref name="sellsCredits"/> is whether this deployment actually offers
    /// credit packs (i.e. payments are on) — see <see cref="PackRule"/> for why that matters.</summary>
    public static string? FirstProblem(BillingOptions options, bool sellsCredits)
    {
        if (options.CostDollarsPerCredit <= 0)
            return "Billing:CostDollarsPerCredit must be greater than zero — it is what one credit costs, "
                 + "and every grant, pack size and ledger conversion divides by it.";

        if (options.CreditMarkup <= 0)
            return "Billing:CreditMarkup must be greater than zero — it is the multiplier from a credit's "
                 + "cost to its retail price.";

        // ⚠️ The COMPUTED price, not the two inputs. Both can be positive and still multiply to less than
        // half a micro, which clamps the divisor to 1 and is the exact harm the two rules above exist to
        // prevent. An anchor of 0.0000001 passes them both.
        if (CreditPricing.RetailMicrosPerCredit(options) <= 1)
            return "Billing:CostDollarsPerCredit × Billing:CreditMarkup gives a credit a retail price of one "
                 + "micro or less, so every existing balance would convert to roughly a million times itself.";

        if (sellsCredits && !BillingCatalog.PacksMatchTheAnchor(options))
            return "Billing:CostDollarsPerCredit / Billing:CreditMarkup no longer produce BillingCatalog's "
                 + "pack sizes. A pack's size is a product decision: update BillingCatalog (and the "
                 + "provider's price ids) deliberately, or restore the anchor.";

        return null;
    }

    /// <summary>Why the pack rule is conditional where the anchor rules are absolute. The anchor guards an
    /// irreversible write, so it is worth refusing to boot over on any box. A pack literal that has drifted
    /// from the anchor is a PRICING mistake — real, and recoverable by editing a file — and it can only
    /// cost anybody money on a deployment that actually sells packs. The family box and the demo box sell
    /// nothing, so on them the old unconditional check turned a retuned markup into a dead app: no
    /// Dashboard, no pantry, no receipt upload, over a number those boxes never read.</summary>
    public const string PackRule =
        "Pack sizes are checked only where payments are configured.";
}

/// <summary>Wires <see cref="BillingOptionsValidation.FirstProblem"/> into the options pipeline so a
/// misconfigured box fails at startup with THE rule it broke, rather than with a generic "validation
/// failed" an operator has to go read source to interpret. <paramref name="sellsCredits"/> is read from
/// configuration once at registration — whether this deployment offers credit packs at all.</summary>
public sealed class BillingOptionsValidator(bool sellsCredits) : IValidateOptions<BillingOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingOptions options) =>
        BillingOptionsValidation.FirstProblem(options, sellsCredits) is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
}
