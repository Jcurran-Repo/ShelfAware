namespace ShelfAware.Core.Prediction;

/// <summary>Steering for the count opt-in (DESIGN.md §13.3): counting earns its keep on slow, deep
/// stock — freezer meat, canned goods, bulk — and is miserable on fast movers, whose count is wrong
/// within days and whose drift check then asks forever. Advice only, never a gate: the household can
/// count anything; the surface just says what they'd be signing up for.</summary>
public static class CountingAdvice
{
    /// <summary>At or under this rebuy rhythm, a count is more upkeep than insight: the shelf turns
    /// over about weekly, so §13.5's projection outruns the attestation almost immediately. A
    /// judgement, the same kind as the engine's 90-day unattested window.</summary>
    public const int FastMoverDays = 10;

    /// <summary>Whether a count on this rhythm would be hard to keep true. A null rhythm (still
    /// learning) is not advised against — there is nothing to say either way, and §13.8's census
    /// stock is exactly the rhythm-less population counting exists for.</summary>
    public static bool IsHardToKeepTrue(double? medianIntervalDays) =>
        medianIntervalDays is { } m && m <= FastMoverDays;
}
