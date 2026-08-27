using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using ShelfAware.Web.Diagnostics;

namespace ShelfAware.Web.Tests;

/// <summary>The app-side read of the CI-written test-status.json — present → parsed, absent or malformed →
/// null (the card then invites regenerating it). Reads from a real temp wwwroot.</summary>
public class TestStatusReaderTests : IDisposable
{
    private readonly string _webRoot =
        Path.Combine(Path.GetTempPath(), "shelfaware-teststatus-" + Guid.NewGuid().ToString("N"));

    public TestStatusReaderTests() => Directory.CreateDirectory(_webRoot);

    public void Dispose()
    {
        try { Directory.Delete(_webRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private sealed class FakeEnv(string webRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Development";
    }

    private TestStatusReader Reader() => new(new FakeEnv(_webRoot), NullLogger<TestStatusReader>.Instance);

    private void WriteStatus(string json) => File.WriteAllText(Path.Combine(_webRoot, "test-status.json"), json);

    [Fact]
    public void Read_returns_null_when_the_file_is_absent() => Assert.Null(Reader().Read());

    [Fact]
    public void Read_parses_a_committed_report()
    {
        WriteStatus("""
        { "generatedAt": "2026-03-02T09:00:00Z", "commitSha": "abcdef1234567", "branch": "master",
          "projects": [ { "name": "Engine", "total": 10, "passed": 10, "failed": 0, "skipped": 0 } ] }
        """);

        var report = Reader().Read();

        Assert.NotNull(report);
        Assert.Equal("master", report!.Branch);
        Assert.Equal(10, report.TotalTests);
        Assert.True(report.AllPassed);
    }

    [Fact]
    public void Read_returns_null_on_malformed_json()
    {
        WriteStatus("{ not json");
        Assert.Null(Reader().Read());
    }
}
