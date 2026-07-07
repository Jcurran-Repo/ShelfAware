namespace ShelfAware.Web.Auth;

/// <summary>The tenancy unit: a group of accounts sharing one pantry. Pantry rows carry
/// <see cref="Id"/> as a plain value (the pantry DB has no FK into this auth-side table).</summary>
public class Household
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";

    /// <summary>Uppercase share code another person enters at registration to join this household.
    /// Possession of the code IS the authorization to join, so it's generated from a CSPRNG and
    /// can be regenerated at any time to cut off further joins.</summary>
    public string InviteCode { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
