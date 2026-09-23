using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Auth;

namespace ShelfAware.Web.Data;

/// <summary>
/// Post-v3 additive-only migrations. <c>EnsureCreated</c> builds the full schema on a fresh DB but
/// never alters an existing one, and the app deliberately has no migrations — so a column added
/// after v3 shipped is applied here as an idempotent <c>ALTER TABLE … ADD COLUMN</c> on startup,
/// and a table added after v3 as an idempotent CREATE (whose DDL comes from EF's own create script,
/// so the migrated file and a fresh file cannot drift apart — pinned by AdditiveSchemaTests).
/// Additive only — DEFAULT-valued columns and whole new tables, both of which existing rows never
/// notice; anything that changes existing data's shape is a fresh-DB change (see PantryDbGuard and
/// the v3 notes in CLAUDE.md; NullableInviteCodeMigration is the one documented exception).
///
/// Both DBs get the same treatment. auth.db was described as "a fresh file per deployment site", which
/// stopped being true the moment a deployment had accounts in it worth keeping — an added column there
/// needs the same ALTER as the pantry's, or the next query fails on a real user's live database.
/// </summary>
public static class AdditiveSchema
{
    public static void Apply(ShelfAwareDbContext db)
    {
        // 2026-07-12: the user's "I checked every line" flag for the in-app accuracy check.
        EnsureColumn(db, table: "Receipts", column: "VerifiedForEval", definition: "INTEGER NOT NULL DEFAULT 0");

        // 2026-07-17: flavor/varietal as per-purchase metadata (like Brand and Size) — the Variety
        // feature. Pre-existing rows get NULL: their variety, if any, is baked into the product name.
        EnsureColumn(db, table: "ReceiptLines", column: "Variety", definition: "TEXT NULL");
        EnsureColumn(db, table: "PurchaseEvents", column: "Variety", definition: "TEXT NULL");

        // 2026-07-18: human-entered expiration dates as per-purchase metadata — the expiration-tracking
        // feature (opt-in per household via SettingKeys.TrackExpirationDates). NULL = no date recorded.
        EnsureColumn(db, table: "ReceiptLines", column: "ExpirationDate", definition: "TEXT NULL");
        EnsureColumn(db, table: "PurchaseEvents", column: "ExpirationDate", definition: "TEXT NULL");

        // 2026-07-18: the meal log — "Ate it" started recording WHEN, for the Reports tab's
        // meals/calories-over-time charts. A brand-new table is invisible to existing rows.
        EnsureTable(db, table: "MealEvents");

        // 2026-07-18: named report configurations (the Reports tab's "Save this report").
        EnsureTable(db, table: "SavedReports");

        // 2026-07-22: which receipt's confirm created the product / taught the alias — provenance
        // for "remove this receipt". Pre-existing rows get NULL (unknown origin), which removal
        // reads as "keep": it only ever deletes what it can prove the receipt did.
        EnsureColumn(db, table: "Products", column: "CreatedByReceiptId", definition: "INTEGER NULL");
        EnsureColumn(db, table: "ProductAliases", column: "TaughtByReceiptId", definition: "INTEGER NULL");

        // 2026-07-28: quantity on hand (DESIGN.md §13) — the first thing in the model that measures STOCK
        // rather than flow. Opt-in per product, so every existing row lands on TrackQuantity = 0 and
        // behaves exactly as it did; NULL QuantityOnHand means UNKNOWN, which is the honest state for a
        // pantry nobody has counted yet.
        EnsureColumn(db, table: "Products", column: "TrackQuantity", definition: "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, table: "Products", column: "QuantityOnHand", definition: "TEXT NULL");
        EnsureColumn(db, table: "Products", column: "QuantityCountedAt", definition: "TEXT NULL");

        // 2026-07-30: when a confirm RAN (v4.1) — lets removal order a confirm against a later human
        // count. NULL on every pre-existing confirmed receipt, which removal reads as "no timestamp to
        // compare" and subtracts exactly as it always did.
        EnsureColumn(db, table: "Receipts", column: "ConfirmedAt", definition: "TEXT NULL");

        // 2026-08-13: household-filed bug reports (the human half of in-app problem reporting).
        // A brand-new table is invisible to existing rows.
        EnsureTable(db, table: "BugReports");

        // 2026-08-14: the admin can mark a report handled. A table the EnsureTable above just
        // created already carries the column (current model); this reaches the deployments whose
        // BugReports table predates it.
        EnsureColumn(db, table: "BugReports", column: "ResolvedAt", definition: "TEXT NULL");

        // 2026-08-25: the admin can PROPOSE a report as fixed for the reporter to confirm (before it's
        // resolved). Same additive shape; reaches deployments whose BugReports table predates it.
        EnsureColumn(db, table: "BugReports", column: "ProposedResolvedAt", definition: "TEXT NULL");

        // 2026-08-25: an optional diagnostic snapshot (environment + recent JS errors + page content) the
        // reporter chose to attach. NULL on pre-existing reports (none was captured). Same additive shape.
        EnsureColumn(db, table: "BugReports", column: "StateJson", definition: "TEXT NULL");

        // 2026-08-15: recipe tags — the cookbook's browsable second axis (like ProductTags for
        // products). A brand-new table is invisible to existing rows.
        EnsureTable(db, table: "RecipeTags");

        // 2026-08-15: an optional photo per recipe (the cookbook). Pre-existing rows get NULL (no photo).
        EnsureColumn(db, table: "Recipes", column: "ImagePath", definition: "TEXT NULL");

        // 2026-08-17: the activity log — every undoable action a household takes, backing per-action undo
        // and the /history page. A brand-new table is invisible to existing rows.
        EnsureTable(db, table: "ActivityEntries");

        // 2026-08-21: the receipt's OWN printed money figures — receipt-level subtotal/tax/total/savings
        // for the /receipts breakdown and the household's running "amount saved". Pre-existing rows get
        // NULL (their totals were never captured; the page falls back to the computed line-item sum).
        // decimal? maps to TEXT in SQLite (as QuantityOnHand does), so the type matches EnsureCreated's.
        EnsureColumn(db, table: "Receipts", column: "Subtotal", definition: "TEXT NULL");
        EnsureColumn(db, table: "Receipts", column: "Tax", definition: "TEXT NULL");
        EnsureColumn(db, table: "Receipts", column: "Total", definition: "TEXT NULL");
        EnsureColumn(db, table: "Receipts", column: "Savings", definition: "TEXT NULL");

        // 2026-08-24: per-day AI COST in micros (subscription phase 2 — dollar-aware usage). AiUsage is a
        // PANTRY table (IHouseholdOwned), so this belongs here, not in the auth overload. Existing rows
        // land on 0 (their cost was never captured); new calls accumulate it. Display-only.
        EnsureColumn(db, table: "AiUsages", column: "CostMicros", definition: "INTEGER NOT NULL DEFAULT 0");

        // 2026-08-26: a soft "this quantity looks like a misread pack count" flag on a confirmed line
        // (a 12-pack read as quantity 12). Stamped at confirm time, surfaced on /receipts. Existing rows
        // land on 0 (None) — they were confirmed before the check existed and are not re-judged. Enum →
        // INTEGER, matching EnsureCreated's mapping and the QuantityFlag.None = 0 default.
        EnsureColumn(db, table: "ReceiptLines", column: "QuantityFlag", definition: "INTEGER NOT NULL DEFAULT 0");

        // 2026-08-30: the human-corrected brand remembered per (merchant, raw text), so an opaque line
        // mis-branded by extraction pre-fills the right brand on the next receipt. Pre-existing aliases
        // get NULL (nothing learned; the review falls back to the extraction's brand). Same additive shape.
        EnsureColumn(db, table: "ProductAliases", column: "LearnedBrand", definition: "TEXT NULL");

        // 2026-08-31: meal planning. The plan and its dated slots are new tables (invisible to existing
        // rows); MealPlans first, since PlannedMeals references it. A brand-new table is invisible to
        // existing rows.
        EnsureTable(db, table: "MealPlans");
        EnsureTable(db, table: "PlannedMeals");

        // 2026-08-31: mark a recipe the planner generated, so the Cookbook can hide it until "kept".
        // Pre-existing recipes land on 0 (false) — user-saved, shown as ever. Enum-free bool → INTEGER.
        EnsureColumn(db, table: "Recipes", column: "PlanGenerated", definition: "INTEGER NOT NULL DEFAULT 0");

        // 2026-08-31: how many servings a generated recipe makes — the base the meal-plan card's serving
        // box scales ingredient amounts from. Pre-existing recipes land on NULL (unknown; the box falls
        // back to a plain ×multiplier). int? → INTEGER NULL, matching EnsureCreated's mapping.
        EnsureColumn(db, table: "Recipes", column: "Servings", definition: "INTEGER NULL");

        // 2026-09-05: Eggs's lookalike-pair memory (per-pair first-seen for his mood + a permanent "they're
        // different" dismissal). A brand-new table — invisible to existing rows.
        EnsureTable(db, table: "LookalikePairs");

        // 2026-09-07: Eggs's lookalike-CLUSTER memory (one row per head word: first-seen for his mood + a
        // permanent "they're all different" dismissal). A brand-new table — invisible to existing rows.
        EnsureTable(db, table: "LookalikeClusters");

        // 2026-09-22: when a receipt was UPLOADED — the rhythm the receipt-reminder banner measures
        // (Core's UploadCadence). Stamped where the row is created, so a receipt abandoned in review
        // still counts as an upload.
        //
        // Pre-existing rows are BACKFILLED from ConfirmedAt, once, only on the boot that adds the column.
        // Additive still: nothing existing changes shape or value, and the new column is the only thing
        // written. Without it an established household — the whole point of a learned cadence — would
        // have no rhythm at all until it had uploaded four more times, i.e. the feature would do nothing
        // for weeks on exactly the boxes that are already running. A confirm is the closest honest
        // reading of an upload we have for an old row (same sitting, in the overwhelming majority); rows
        // with no ConfirmedAt stay NULL rather than being guessed at from the printed purchase date,
        // which is the one date that is NOT an upload.
        if (EnsureColumn(db, table: "Receipts", column: "UploadedAt", definition: "TEXT NULL"))
            Execute(db, "UPDATE Receipts SET UploadedAt = ConfirmedAt WHERE ConfirmedAt IS NOT NULL;");
    }

    public static void Apply(AuthDbContext db)
    {
        // 2026-07-15: invite codes stopped being permanent, unlimited bearer credentials. Existing codes
        // get NULL/0 — no expiry, no use limit — which is exactly what they already were, so a live
        // deployment's outstanding invites keep working until someone regenerates them.
        EnsureColumn(db, table: "Households", column: "InviteExpiresAt", definition: "TEXT NULL");
        EnsureColumn(db, table: "Households", column: "InviteMaxUses", definition: "INTEGER NULL");
        EnsureColumn(db, table: "Households", column: "InviteUseCount", definition: "INTEGER NOT NULL DEFAULT 0");

        // 2026-08-13: the in-app error log (deduped Error/Critical events; operator data, so it
        // lives here rather than in any household's pantry). A new table — existing rows unaffected.
        EnsureTable(db, table: "ErrorLog");

        // 2026-08-14: resolved errors leave the admin's open list until they recur.
        EnsureColumn(db, table: "ErrorLog", column: "ResolvedAt", definition: "TEXT NULL");

        // 2026-08-22: API tokens for the read-only GraphQL API. Credentials, so they live in auth.db
        // beside accounts and invite codes (the lookup is the auth step — it happens before any
        // household is known, so this can't be a household-filtered pantry table). A new table —
        // existing rows unaffected.
        EnsureTable(db, table: "ApiTokens");

        // 2026-08-23: per-account login counts (the admin "who has logged in" view). Operator data, like
        // the error log, so it lives here. A new table — existing rows unaffected.
        EnsureTable(db, table: "UserLoginStats");

        // 2026-08-24: the credit ledger (subscription phase 2 — the money record). Auth-side beside the
        // subscription, so it survives a pantry "delete my data". A new table — existing rows unaffected.
        EnsureTable(db, table: "CreditLedger");

        // 2026-09-19: which consumption a Reversal row hands back (remediation phase 7). Existing rows land
        // on NULL, which is exactly right: every row written before this existed is a kind that never
        // reverses anything. ⚠️ The unspent-allowance sum reads it — a Reversal it cannot attribute is left
        // out of that sum rather than guessed at, which is the direction that cannot eat purchased credit.
        EnsureColumn(db, table: "CreditLedger", column: "ReversesEntryId", definition: "INTEGER NULL");

        // 2026-08-24: household entitlement tiers (docs/subscription-plan.md phase 1 — the Founder tier
        // + the subscription seam). Tier is an enum → INTEGER, so existing rows land on Free (0) with no
        // FounderSince, which behaves exactly as a pre-tier household did.
        EnsureColumn(db, table: "Households", column: "Tier", definition: "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, table: "Households", column: "FounderSince", definition: "TEXT NULL");

        // 2026-09-01: subscription state (docs/subscription-plan.md phase 3 step 1 — the payment
        // foundation). Provider customer/subscription ids + period state on the household. Existing rows
        // land on NULL/0 — never subscribed — which is the resting state and how a pre-billing box reads.
        // ⚠️ These are Households columns, so NullableInviteCodeMigration must know them too: they're in
        // its ExpectedColumns and COPIED (not wiped) by its rebuild, the way Tier/FounderSince are.
        EnsureColumn(db, table: "Households", column: "BillingCustomerId", definition: "TEXT NULL");
        EnsureColumn(db, table: "Households", column: "SubscriptionId", definition: "TEXT NULL");
        EnsureColumn(db, table: "Households", column: "SubscriptionRenewsAt", definition: "TEXT NULL");
        EnsureColumn(db, table: "Households", column: "SubscriptionCancelAtPeriodEnd", definition: "INTEGER NOT NULL DEFAULT 0");

        // 2026-09-02: the lazy per-month Aware allowance marker (subscription phase 4a — enforcement). Holds
        // the CALENDAR MONTH (CreditLedger.PeriodFor — first-of-month UTC) the current monthly allowance was
        // granted for, so the grant is idempotent within a month and rolls over at the next. Existing rows
        // land on NULL (no allowance granted yet).
        // ⚠️ A Households column → NullableInviteCodeMigration must list it in ExpectedColumns (copied, not
        // wiped) like Tier/Subscription*.
        EnsureColumn(db, table: "Households", column: "AllowanceGrantedForPeriod", definition: "TEXT NULL");

        // 2026-08-30: the /about wishlist — pre-launch demand for a hosted Reginald. Operator data,
        // like the error log, so it lives here rather than in any household's pantry. A new table —
        // existing rows unaffected.
        EnsureTable(db, table: "Wishlist");

        // 2026-09-03: when an account was created (server-local day) — feeds the demo box's daily
        // account-creation cap (§10). Pre-existing accounts get NULL (unknown day), which never matches
        // "== today" and so never counts toward the cap. DateOnly? → TEXT NULL, matching EnsureCreated's.
        EnsureColumn(db, table: "AspNetUsers", column: "CreatedOn", definition: "TEXT NULL");

        // 2026-09-01: payment webhook idempotency (phase 3 step 2) — one row per applied provider event,
        // so a retried webhook can't double-apply. Auth-side beside the subscription + ledger. A new
        // table — existing rows unaffected.
        EnsureTable(db, table: "ProcessedPaymentEvents");

        // 2026-09-05: the managed demo box's box-wide daily AI counters (§10) — operator data, like the
        // error log. A new table — existing rows unaffected, and unwritten unless a Demo cap is configured.
        EnsureTable(db, table: "DemoUsage");

        // 2026-09-19: per-day, per-action reconciliation of credits charged against provider cost (the
        // Shelf Aware credit, docs/remediation-plan.md §7) — operator data, box-wide, like DemoUsage above.
        // A new table — existing rows unaffected.
        EnsureTable(db, table: "ServiceMargin");

        // 2026-09-19: the part of a day's cost that a household was actually on the hook for, split out from
        // the all-calls total so /admin's cost-per-charge divides one population (docs/remediation-plan.md
        // §9). EnsureTable above returns early when the table exists, so a box booted off the branch between
        // these two commits would never get the column — and ServiceMarginMeter's best-effort catch would
        // swallow the "no such column" forever while /admin threw. One line, and the drill is the drill.
        EnsureColumn(db, table: "ServiceMargin", column: "BillableCostMicros", definition: "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>Run one statement on the context's own connection — the backfill half of an additive
    /// change, on the same connection the ALTER above used.</summary>
    private static void Execute(DbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using var command = conn.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    /// <summary>Create <paramref name="table"/> (and its indexes) on a DB built before it existed. The
    /// DDL is not hand-written: it's lifted from <c>GenerateCreateScript()</c> — the exact statements
    /// EnsureCreated runs on a fresh file — so there is no second copy of the schema to keep honest.
    /// A model change to the table automatically changes what gets created here.</summary>
    private static void EnsureTable(DbContext db, string table)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText =
                "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name);";
            var nameParam = check.CreateParameter();
            nameParam.ParameterName = "@name";
            nameParam.Value = table;
            check.Parameters.Add(nameParam);
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;

            foreach (var statement in StatementsFor(db, table))
            {
                using var create = conn.CreateCommand();
                create.CommandText = statement;
                create.ExecuteNonQuery();
            }
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }

    /// <summary>The create-script statements that build <paramref name="table"/>: its CREATE TABLE and
    /// every CREATE INDEX on it. Splitting the script on ";" is safe because this schema's DDL contains
    /// no embedded semicolons (identifiers and defaults are plain; nothing user-supplied is in the
    /// script) — and the schema-parity test would fail the moment a model change broke that assumption,
    /// because the mangled statement wouldn't rebuild the fresh schema.</summary>
    private static IEnumerable<string> StatementsFor(DbContext db, string table)
    {
        var script = db.Database.GenerateCreateScript();
        var statements = script
            .Split(';')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);
        foreach (var statement in statements)
        {
            if (statement.StartsWith($"CREATE TABLE \"{table}\"", StringComparison.Ordinal) ||
                (statement.StartsWith("CREATE ", StringComparison.Ordinal) &&
                 statement.Contains($" ON \"{table}\" ", StringComparison.Ordinal)))
            {
                yield return statement;
            }
        }
    }

    /// <summary>Add <paramref name="column"/> to <paramref name="table"/> if it isn't there yet.
    /// Returns true only when this call actually added it — which is the one safe moment to BACKFILL the
    /// new column from an existing one, since a later boot must never re-run a backfill over values the
    /// app has since written.</summary>
    private static bool EnsureColumn(DbContext db, string table, string column, string definition)
    {
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed) conn.Open();
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText =
                $"SELECT EXISTS (SELECT 1 FROM pragma_table_info('{table}') WHERE name = '{column}');";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return false;

            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
            return true;
        }
        finally
        {
            if (wasClosed) conn.Close();
        }
    }
}
