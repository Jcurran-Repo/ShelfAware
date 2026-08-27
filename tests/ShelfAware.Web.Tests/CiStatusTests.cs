using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.Tests;

public class CiStatusTests
{
    private const string TwoWorkflowsJson = """
    {
      "workflow_runs": [
        { "name": "CI", "status": "completed", "conclusion": "failure", "head_branch": "feature/x",
          "head_sha": "0000000oldsha", "display_title": "an earlier CI run",
          "updated_at": "2026-03-01T10:00:00Z", "html_url": "https://gh/runs/1" },
        { "name": "CI", "status": "completed", "conclusion": "success", "head_branch": "master",
          "head_sha": "1111111newsha", "display_title": "the latest CI run",
          "updated_at": "2026-03-02T10:00:00Z", "html_url": "https://gh/runs/2" },
        { "name": "Mutation", "status": "completed", "conclusion": "success", "head_branch": "master",
          "head_sha": "2222222mut", "display_title": "weekly mutation run",
          "updated_at": "2026-03-01T06:00:00Z", "html_url": "https://gh/runs/3" }
      ]
    }
    """;

    [Fact]
    public void Parse_keeps_only_the_latest_run_of_each_workflow_name_ordered()
    {
        var runs = CiStatusParser.Parse(TwoWorkflowsJson);

        Assert.Equal(2, runs.Count);                 // CI and Mutation, not three runs
        Assert.Equal("CI", runs[0].Workflow);        // name-ordered: CI before Mutation
        Assert.Equal("Mutation", runs[1].Workflow);
        // CI's kept run is the NEWER one (03-02), not the older failure — proves latest-per-workflow.
        Assert.Equal("success", runs[0].Conclusion);
        Assert.Equal("https://gh/runs/2", runs[0].Url);
    }

    [Fact]
    public void Parse_maps_every_field()
    {
        var run = Assert.Single(CiStatusParser.Parse("""
        { "workflow_runs": [ { "name": "CI", "status": "completed", "conclusion": "success",
          "head_branch": "master", "head_sha": "abcdef1234567", "display_title": "green",
          "updated_at": "2026-03-02T09:30:00Z", "html_url": "https://gh/runs/9" } ] }
        """));

        Assert.Equal("CI", run.Workflow);
        Assert.Equal("completed", run.Status);
        Assert.Equal("success", run.Conclusion);
        Assert.Equal("master", run.Branch);
        Assert.Equal("abcdef1234567", run.Sha);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 9, 30, 0, TimeSpan.Zero), run.UpdatedAt);
        Assert.Equal("https://gh/runs/9", run.Url);
    }

    [Theory]
    [InlineData("""{ "workflow_runs": [] }""")]
    [InlineData("""{ "something_else": 1 }""")]
    [InlineData("""{ "workflow_runs": "not an array" }""")]
    public void Parse_returns_empty_when_there_are_no_runs(string json) =>
        Assert.Empty(CiStatusParser.Parse(json));

    [Theory]
    [InlineData("completed", "success", CiOutcome.Passed)]
    [InlineData("completed", "failure", CiOutcome.Failed)]
    [InlineData("completed", "timed_out", CiOutcome.Failed)]
    [InlineData("completed", "startup_failure", CiOutcome.Failed)]
    [InlineData("completed", "cancelled", CiOutcome.Other)]
    [InlineData("completed", "skipped", CiOutcome.Other)]
    [InlineData("in_progress", null, CiOutcome.Running)]
    [InlineData("queued", null, CiOutcome.Running)]
    [InlineData("something_odd", null, CiOutcome.Other)]
    public void Outcome_collapses_status_and_conclusion(string status, string? conclusion, CiOutcome expected)
    {
        var run = new CiRun("CI", status, conclusion, "master", "abc1234", DateTimeOffset.Now, "u");
        Assert.Equal(expected, run.Outcome);
    }

    [Fact]
    public void ShortSha_is_the_first_seven_or_the_whole_thing_when_shorter()
    {
        Assert.Equal("abcdef1", new CiRun("CI", "completed", "success", "m", "abcdef1234567890", DateTimeOffset.Now, "u").ShortSha);
        Assert.Equal("abc", new CiRun("CI", "completed", "success", "m", "abc", DateTimeOffset.Now, "u").ShortSha);
    }
}
