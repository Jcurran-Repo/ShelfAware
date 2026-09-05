namespace ShelfAware.Core.Shopping;

/// <summary>How Eggs feels about a lookalike pair he flagged and you haven't dealt with yet. His mood — and
/// only his mood, plus one line — degrades the longer the pair lingers; the ask itself stays a gentle
/// "merge them?" the whole way. A merge or a dismiss makes the pair go away, so he's instantly at peace.</summary>
public enum NudgeMood
{
    /// <summary>Just spotted it — cheery.</summary>
    Fresh,
    /// <summary>A few days on — a bit deflated.</summary>
    Deflating,
    /// <summary>A week in — it's really getting to him.</summary>
    Nagging,
    /// <summary>Two weeks+ — peak Meeseeks, comedically frazzled (never actually distressing).</summary>
    Frazzled,
}

/// <summary>The mood a flagged pair has aged into, and Eggs's line for it. Pure so both are unit-tested and
/// live in ONE place — the mascot's expression and the card's copy read the same enum.</summary>
public static class NudgeMoods
{
    // Days-since-first-flagged thresholds (Jordan's pacing: cheery for a couple of days, deflating over the
    // week, frazzled by two). Named so the pace is one obvious tuning knob.
    public const int DeflatingAfterDays = 3;
    public const int NaggingAfterDays = 7;
    public const int FrazzledAfterDays = 14;

    /// <summary>The mood for a pair first flagged <paramref name="age"/> ago. A negative age (a clock that
    /// went backwards) reads as Fresh, never a crash.</summary>
    public static NudgeMood For(TimeSpan age)
    {
        var days = age.TotalDays;
        if (days < DeflatingAfterDays) return NudgeMood.Fresh;
        if (days < NaggingAfterDays) return NudgeMood.Deflating;
        if (days < FrazzledAfterDays) return NudgeMood.Nagging;
        return NudgeMood.Frazzled;
    }

    /// <summary>Eggs's line for the mood — generic, since the card names the two products beside it.</summary>
    public static string Line(NudgeMood mood) => mood switch
    {
        NudgeMood.Fresh => "Ooh — these two look like the same thing to me.",
        NudgeMood.Deflating => "…still seeing double over here.",
        NudgeMood.Nagging => "These twins are really nagging at me.",
        NudgeMood.Frazzled => "I can't look at these two anymore!",
        _ => "Ooh — these two look like the same thing to me.",
    };
}
