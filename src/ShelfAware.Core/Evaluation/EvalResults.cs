namespace ShelfAware.Core.Evaluation;

/// <summary>
/// Extraction-accuracy results (DESIGN.md §9). Written by the eval harness
/// (tests/ShelfAware.Evals) and rendered by the Accuracy page. Targets: ≥90% recall,
/// ≥90% precision, ≥85% field accuracy.
/// </summary>
public record EvalResults
{
    public DateTimeOffset GeneratedAt { get; init; }
    public string Model { get; init; } = "";
    public EvalAggregate Aggregate { get; init; } = new();
    public IReadOnlyList<FixtureScore> Fixtures { get; init; } = [];
}

public record EvalAggregate
{
    public double Recall { get; init; }
    public double Precision { get; init; }
    public double FieldAccuracy { get; init; }
}

public record FixtureScore
{
    public string Name { get; init; } = "";
    public int ExpectedLines { get; init; }
    public int FoundLines { get; init; }
    public int MatchedLines { get; init; }
    public double Recall { get; init; }
    public double Precision { get; init; }
    public double FieldAccuracy { get; init; }
    public string? Error { get; init; }
}
