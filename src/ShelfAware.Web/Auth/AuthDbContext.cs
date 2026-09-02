using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ShelfAware.Web.Diagnostics;
using ShelfAware.Web.Wishlist;

namespace ShelfAware.Web.Auth;

/// <summary>Identity + household store, in its OWN SQLite file (<c>auth.db</c>), separate from the
/// pantry DB. Deliberate: the pantry context stays free of Identity noise, and a brand-new file means
/// <c>EnsureCreated</c> builds the full auth schema on every deployment — no migrations, matching the
/// project-wide rule. Accounts hold only credentials (password HASHES via Identity's hasher) and the
/// household link; nothing here is ever exported or rendered beyond member emails. This file is also
/// where app-level OPERATOR data lives (the error log): rows no household owns, outside the pantry
/// context's tenancy machinery on purpose — putting them there would either fake a household owner
/// or punch a hole in the query filter, and both are worse than a second table here.</summary>
public class AuthDbContext(DbContextOptions<AuthDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Household> Households => Set<Household>();
    public DbSet<ErrorLogEntry> ErrorLog => Set<ErrorLogEntry>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<UserLoginStat> UserLoginStats => Set<UserLoginStat>();
    public DbSet<CreditLedgerEntry> CreditLedger => Set<CreditLedgerEntry>();

    /// <summary>Idempotency ledger for payment webhooks (phase 3) — one row per applied provider event.
    /// See <see cref="ProcessedPaymentEvent"/>.</summary>
    public DbSet<ProcessedPaymentEvent> ProcessedPaymentEvents => Set<ProcessedPaymentEvent>();

    /// <summary>Pre-launch demand for a HOSTED Reginald — operator data, same rationale as the error
    /// log above. No index: like ErrorLog, the table is bounded and ordered/deduped client-side (SQLite
    /// can't ORDER BY a DateTimeOffset in SQL), so an index would serve nothing.</summary>
    public DbSet<WishlistEntry> Wishlist => Set<WishlistEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Unique, and deliberately NOT filtered: SQLite treats NULLs as distinct in a unique index, so
        // every code-less household coexists here while two households could never share a live code.
        // That property is the whole reason Household.InviteCode is null rather than "" — don't "tidy"
        // the column back to non-nullable.
        modelBuilder.Entity<Household>().HasIndex(h => h.InviteCode).IsUnique();
        modelBuilder.Entity<AppUser>().HasIndex(u => u.HouseholdId);

        // Derived from InviteMaxUses/InviteUseCount for display — behaviour, not stored state. EF maps
        // computed properties as columns by convention unless told otherwise (the same trap Recipe's
        // IsVariant hit), and a phantom column here would break EnsureCreated against a live auth.db.
        modelBuilder.Entity<Household>().Ignore(h => h.InviteUsesRemaining);

        // One row per distinct error (the dedupe upsert's anchor). No index on LastSeenAt: SQLite
        // can't ORDER BY a DateTimeOffset in SQL, so ordering/trimming happen client-side over the
        // bounded table and an index there would serve nothing.
        modelBuilder.Entity<ErrorLogEntry>().HasIndex(e => e.Fingerprint).IsUnique();
        // Derived from ResolvedAt/LastSeenAt for display — same trap and same fix as
        // InviteUsesRemaining above: a phantom column would break EnsureCreated against a live auth.db.
        modelBuilder.Entity<ErrorLogEntry>().Ignore(e => e.Resolved);

        // Unique on the hash: the auth handler looks a presented token up by TokenHash, so the index is
        // both the lookup path and a guarantee two rows can't share a hash. Indexed by household so
        // listing a household's tokens (and revoking them all on delete-my-data) doesn't table-scan.
        modelBuilder.Entity<ApiToken>().HasIndex(t => t.TokenHash).IsUnique();
        modelBuilder.Entity<ApiToken>().HasIndex(t => t.HouseholdId);

        // One row per account, keyed on the Identity user id (not the convention's "Id"), which is what
        // makes LoginAudit's upsert a constraint-guarded increment rather than a growing event log.
        modelBuilder.Entity<UserLoginStat>().HasKey(s => s.UserId);

        // Indexed by household so summing a household's balance (and exporting/reading its ledger)
        // doesn't table-scan — the hand-scoped read path, since auth.db has no query filter.
        modelBuilder.Entity<CreditLedgerEntry>().HasIndex(e => e.HouseholdId);

        // The provider's event id IS the key — a webhook is looked up and deduped by it, and making it
        // the PK gives the unique constraint the concurrent-duplicate insert race relies on (a second
        // delivery of the same event loses the insert). String key, like UserLoginStat's UserId.
        modelBuilder.Entity<ProcessedPaymentEvent>().HasKey(e => e.EventId);
    }
}
