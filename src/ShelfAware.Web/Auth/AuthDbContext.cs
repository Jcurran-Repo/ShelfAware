using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ShelfAware.Web.Auth;

/// <summary>Identity + household store, in its OWN SQLite file (<c>auth.db</c>), separate from the
/// pantry DB. Deliberate: the pantry context stays free of Identity noise, and a brand-new file means
/// <c>EnsureCreated</c> builds the full auth schema on every deployment — no migrations, matching the
/// project-wide rule. Accounts hold only credentials (password HASHES via Identity's hasher) and the
/// household link; nothing here is ever exported or rendered beyond member emails.</summary>
public class AuthDbContext(DbContextOptions<AuthDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Household> Households => Set<Household>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Household>().HasIndex(h => h.InviteCode).IsUnique();
        modelBuilder.Entity<AppUser>().HasIndex(u => u.HouseholdId);
    }
}
