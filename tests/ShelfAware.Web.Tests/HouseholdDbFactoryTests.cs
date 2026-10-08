using ShelfAware.Web.Data;

namespace ShelfAware.Web.Tests;

/// <summary>THE way to a pantry context. Its whole job is that nothing gets an unscoped context by
/// accident — so the two behaviors that matter are "the context carries the resolved household" and
/// "no household means NO context", never a silent fall-through to nobody's pantry.</summary>
public sealed class HouseholdDbFactoryTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_context_comes_back_scoped_to_the_resolved_household()
    {
        var factory = new HouseholdDbFactory(_db, new FakeCurrentHousehold("hh-resolved"), new FixedMember("member-1"));

        await using var db = await factory.CreateDbContextAsync();

        Assert.Equal("hh-resolved", db.HouseholdId);
    }

    [Fact]
    public async Task No_resolvable_household_refuses_rather_than_handing_out_an_unscoped_context()
    {
        var factory = new HouseholdDbFactory(_db, new FakeCurrentHousehold(id: null), new FixedMember("member-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateDbContextAsync());
    }

    private sealed class FixedMember(string? id) : ICurrentMember
    {
        public ValueTask<string?> GetMemberIdAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(id);
        public void UseFixedMember(string memberId) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_context_carries_the_person_for_the_per_member_tables()
    {
        var factory = new HouseholdDbFactory(_db, new FakeCurrentHousehold("hh-resolved"), new FixedMember("member-1"));

        await using var db = await factory.CreateDbContextAsync();

        Assert.Equal("member-1", db.MemberId);
    }

    [Fact]
    public async Task No_person_still_gives_a_household_context_that_knows_nobody()
    {
        // The household tables work as ever; only the journal is out of reach (it reads nothing, writes nothing).
        var factory = new HouseholdDbFactory(_db, new FakeCurrentHousehold("hh-resolved"), new FixedMember(null));

        await using var db = await factory.CreateDbContextAsync();

        Assert.Equal("hh-resolved", db.HouseholdId);
        Assert.Null(db.MemberId);
    }
}
