namespace ShelfAware.Web.Auth;

/// <summary>Thrown when the demo box's BOX-WIDE daily AI cap is hit — the wallet valve, distinct from the
/// per-household cap (which throws its own message from <c>AiUsageMeter</c>). Carries the polite come-back
/// message so a surface that shows the exception text says the right thing; the pre-check normally returns
/// that same message before a call ever reaches the throw.</summary>
public sealed class DemoDailyCapException() : Exception(DemoLimits.DailyCapReachedMessage);
