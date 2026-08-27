using ShelfAware.Core.Evaluation;

namespace ShelfAware.Tests;

public class TrxSummaryTests
{
    // Distinct total/passed/failed/notExecuted so a swapped or mis-named counter can't pass.
    private const string Trx = """
    <?xml version="1.0" encoding="UTF-8"?>
    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
      <ResultSummary outcome="Completed">
        <Counters total="10" executed="9" passed="7" failed="2" notExecuted="1" />
      </ResultSummary>
    </TestRun>
    """;

    [Fact]
    public void Parse_reads_the_counters_into_the_named_project()
    {
        var result = TrxSummary.Parse("Engine", Trx);

        Assert.Equal("Engine", result.Name);
        Assert.Equal(10, result.Total);
        Assert.Equal(7, result.Passed);
        Assert.Equal(2, result.Failed);
        Assert.Equal(1, result.Skipped); // notExecuted
    }

    [Fact]
    public void Parse_reads_zeros_when_there_is_no_summary()
    {
        var result = TrxSummary.Parse(
            "Empty", """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" />""");

        Assert.Equal(new TestProjectResult("Empty", 0, 0, 0, 0), result);
    }

    [Fact]
    public void Parse_reads_zero_for_a_non_numeric_counter()
    {
        var result = TrxSummary.Parse("X", """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <ResultSummary><Counters total="oops" passed="5" /></ResultSummary>
        </TestRun>
        """);

        Assert.Equal(0, result.Total);  // "oops" → 0, not a throw
        Assert.Equal(5, result.Passed);
    }

    [Fact]
    public void Parse_folds_error_timeout_and_aborted_into_failed()
    {
        // A green "Tests & quality" card must not hide a test that errored or timed out rather than failing
        // an assertion — Failed sums all non-passing completed outcomes. Distinct values so dropping any one
        // is caught.
        var result = TrxSummary.Parse("X", """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <ResultSummary><Counters total="28" passed="10" failed="2" error="3" timeout="5" aborted="7" notExecuted="1" /></ResultSummary>
        </TestRun>
        """);

        Assert.Equal(2 + 3 + 5 + 7, result.Failed); // failed + error + timeout + aborted = 17
        Assert.Equal(10, result.Passed);
        Assert.Equal(1, result.Skipped);
    }
}
