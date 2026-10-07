namespace ShelfAware.Web.Services;

/// <summary>The come-back-later pre-check for a UI surface on a MANAGED-key deployment: the reason a
/// host-key LLM call would be refused by one of the daily caps today, or null. Split out from the concrete
/// DB-backed meters so <see cref="AiErrorText"/> can ask the question through a seam, and its tests can
/// answer it without a database. A total no-op on a family / self-host box with no caps configured.</summary>
public interface IManagedCallCaps
{
    ValueTask<string?> CallBlockedMessageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// THE pre-check twin of the two cap checks <c>MeteredChatClient.EnsureManagedCallAllowedAsync</c> runs
/// before a host-key provider call — the household's own daily allowance (<see cref="AiUsageMeter"/>), then
/// the box-wide demo valve (<see cref="DemoUsageMeter"/>) — asked in the same order, so a surface and the
/// server-side gate can never disagree about which of them said no.
///
/// <para>Until 2026-10-07 the pre-check mirrored only the box-wide valve. A household that had spent its
/// own allowance passed the pre-check, made the call, had the gate refuse it, and was shown the surface's
/// generic "couldn't reach the AI — please try again": a true-sounding sentence whose advice was the one
/// thing that could not help. Scoped, because the household meter is.</para>
/// </summary>
public sealed class ManagedCallCaps(AiUsageMeter household, DemoUsageMeter boxWide) : IManagedCallCaps
{
    public async ValueTask<string?> CallBlockedMessageAsync(CancellationToken cancellationToken = default)
        => await household.DailyCapBlockedMessageAsync(cancellationToken)
           ?? await boxWide.CallBlockedMessageAsync(cancellationToken);
}
