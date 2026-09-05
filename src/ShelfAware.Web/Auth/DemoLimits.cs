namespace ShelfAware.Web.Auth;

/// <summary>Shared wording for the demo box's daily caps: the account-creation cap and the box-wide AI
/// valve (<see cref="DemoUsageMeter"/>, docs/subscription-plan.md §10) hand the visitor the SAME polite
/// "come back tomorrow" message, so the two can't drift into differently-worded ways of saying the same thing.
///
/// Not an "Error:" — it doesn't start with that word, so the auth pages' <c>StatusMessage</c> renders it as
/// a calm notice rather than a red failure. It IS blocking, but the visitor did nothing wrong.</summary>
public static class DemoLimits
{
    public const string DailyCapReachedMessage =
        "This demo box is usage-limited and has hit today's limit — please come back tomorrow.";
}
