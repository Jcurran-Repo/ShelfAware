using ShelfAware.Core.Evaluation;

namespace ShelfAware.Tests;

public class TestStatusReportTests
{
    [Fact]
    public void Totals_sum_across_projects()
    {
        // Every metric has DISTINCT per-project values so each Total is a genuine SUM, not the max of one
        // project — otherwise a Sum→Max mutation survives (max == sum when one project's value is 0).
        var report = new TestStatusReport
        {
            Projects =
            [
                new TestProjectResult("A", Total: 10, Passed: 5, Failed: 3, Skipped: 2),
                new TestProjectResult("B", Total: 20, Passed: 14, Failed: 5, Skipped: 1),
            ],
        };

        Assert.Equal(30, report.TotalTests);
        Assert.Equal(19, report.TotalPassed);
        Assert.Equal(8, report.TotalFailed);
        Assert.Equal(3, report.TotalSkipped);
    }

    [Fact]
    public void An_unset_report_has_empty_commit_and_branch()
    {
        // The defaults are "" — the card hides the "From the run on…" line on an empty sha, so a non-empty
        // default would show a bogus one. (Also pins the empty ShortSha path.)
        var report = new TestStatusReport();

        Assert.Equal("", report.CommitSha);
        Assert.Equal("", report.Branch);
        Assert.Equal("", report.ShortSha);
    }

    [Fact]
    public void AllPassed_needs_at_least_one_test_and_no_failures()
    {
        Assert.True(new TestStatusReport { Projects = [new("A", 5, 5, 0, 0)] }.AllPassed);
        Assert.False(new TestStatusReport { Projects = [new("A", 5, 4, 1, 0)] }.AllPassed); // a failure
        Assert.False(new TestStatusReport { Projects = [] }.AllPassed);                     // nothing ran
    }

    [Fact]
    public void ShortSha_is_the_first_seven_or_the_whole_thing_when_shorter()
    {
        Assert.Equal("abcdef1", new TestStatusReport { CommitSha = "abcdef1234567" }.ShortSha);
        Assert.Equal("abc", new TestStatusReport { CommitSha = "abc" }.ShortSha);
        Assert.Equal("", new TestStatusReport { CommitSha = "" }.ShortSha);
    }
}
