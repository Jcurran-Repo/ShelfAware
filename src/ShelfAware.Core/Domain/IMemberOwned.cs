namespace ShelfAware.Core.Domain;

/// <summary>Marks an entity as belonging to ONE PERSON inside a household — a narrower owner than
/// <see cref="IHouseholdOwned"/>, which every such entity also is. Everything else in the pantry is the
/// household's to share; a meal journal is not, because a calorie total only means something about the
/// person who ate it.
///
/// <para>The Web DbContext filters these on household AND member and stamps the member on insert, the
/// same structural guard the household gets — so no page or service has to remember a WHERE clause to
/// keep one member's journal out of another's screen. <see cref="MemberId"/> is non-nullable on purpose:
/// a context with no member compares it to null and EF folds that to FALSE, so code that never learned
/// who is asking reads nothing rather than everyone's (the same trick <c>AppSetting</c>'s key uses).</para></summary>
public interface IMemberOwned : IHouseholdOwned
{
    /// <summary>The Identity user id of the person this row belongs to. The CLR default "" only exists so
    /// an Added row has a stampable value.</summary>
    string MemberId { get; set; }
}
