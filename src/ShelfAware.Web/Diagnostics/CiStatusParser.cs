using System.Text.Json;

namespace ShelfAware.Web.Diagnostics;

/// <summary>Turns a GitHub Actions "list workflow runs" payload
/// (<c>GET /repos/{owner}/{repo}/actions/runs</c>) into the latest run of each workflow. Pure and
/// defensive: a missing or oddly-typed field on one run degrades to a sane default rather than throwing,
/// so a single unexpected run can't blank the whole card. Genuinely malformed JSON does throw — the
/// service catches it and shows the error state.</summary>
public static class CiStatusParser
{
    public static IReadOnlyList<CiRun> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("workflow_runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var all = new List<CiRun>();
        foreach (var r in runs.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            all.Add(new CiRun(
                Workflow: Str(r, "name") ?? "(workflow)",
                Status: Str(r, "status") ?? "",
                Conclusion: Str(r, "conclusion"),
                Branch: Str(r, "head_branch") ?? "",
                Sha: Str(r, "head_sha") ?? "",
                UpdatedAt: Time(r, "updated_at"),
                Url: Str(r, "html_url") ?? ""));
        }

        // One card per workflow: the most recent run of each (by updated_at), then workflow-name ordered so
        // the card is stable render to render.
        return all
            .GroupBy(r => r.Workflow)
            .Select(g => g.OrderByDescending(r => r.UpdatedAt).First())
            .OrderBy(r => r.Workflow, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset Time(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : default;
}
