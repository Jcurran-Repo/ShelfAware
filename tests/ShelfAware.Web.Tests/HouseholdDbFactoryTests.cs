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
        var factory = new HouseholdDbFactory(_db, new FakeCurrentHousehold("hh-resolved"));

        await using var db = await factory.CreateDbContextAsync();

        Assert.Equal("hh-resolved", db.HouseholdId);
    }

    [Fact]
    public async Task No_resolvable_household_refuses_rather_than_handing_out_an_unscoped_context()
    {
        var factory = new HouseholdDbFactory(_db, new FakeCurrentHousehold(id: null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateDbContextAsync());
    }
}
