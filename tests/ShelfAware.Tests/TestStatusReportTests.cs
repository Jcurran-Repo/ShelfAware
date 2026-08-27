using ShelfAware.Core.Evaluation;

namespace ShelfAware.Tests;

public class TestStatusReportTests
{
    [Fact]
    public void Totals_sum_across_projects()
    {
        var report = new TestStatusReport
        {
            Projects =
            [
                new TestProjectResult("A", Total: 10, Passed: 8, Failed: 1, Skipped: 1),
                new TestProjectResult("B", Total: 20, Passed: 19, Failed: 0, Skipped: 1),
            ],
        };

        Assert.Equal(30, report.TotalTests);
        Assert.Equal(27, report.TotalPassed);
        Assert.Equal(1, report.TotalFailed);
        Assert.Equal(2, report.TotalSkipped);
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
