using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using ShelfAware.Core.Evaluation;

namespace ShelfAware.Web.Diagnostics;

/// <summary>Reads the CI-written <c>test-status.json</c> for the admin dashboard's "Tests &amp; quality"
/// card. A seam so the page can be tested against a fake and the file-reading implementation on its own.</summary>
public interface ITestStatusProvider
{
    TestStatusReport? Read();
}

/// <summary>Reads <c>wwwroot/test-status.json</c> — committed and served like <c>eval-results.json</c>, so
/// it's as fresh as the last regenerate-and-deploy (the report's own GeneratedAt/sha make that visible on
/// the card). Missing or unreadable reads as null; the card then invites regenerating it.</summary>
public sealed class TestStatusReader(IWebHostEnvironment env, ILogger<TestStatusReader> logger) : ITestStatusProvider
{
    public TestStatusReport? Read()
    {
        try
        {
            var path = Path.Combine(env.WebRootPath, "test-status.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TestStatusReport>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't read test-status.json.");
            return null;
        }
    }
}
