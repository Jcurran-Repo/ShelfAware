using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShelfAware.Web.Diagnostics;

/// <summary>The diagnostic snapshot a reporter may attach to a bug report — the shape of
/// <c>BugReport.StateJson</c>. Captured client-side (<c>wwwroot/js/bug-capture.js</c>) at the moment they
/// click "Report a bug", then shown on the form with each section independently removable; whichever
/// sections survive are serialized here and stored. Both halves are nullable because either can be dropped
/// (or be absent on a pre-feature row / a direct /bugs visit), and parsing is deliberately forgiving so a
/// partial or legacy blob degrades to "show what's there", never an error on the admin's screen.</summary>
public sealed record BugReportSnapshot(BugDiagnostics? Diagnostics, string? PageContent)
{
    // Blazor JS interop deserializes with the Web defaults (camelCase, case-insensitive); using the same
    // options here keeps the interop payload and the stored blob identical in shape, so a round-trip
    // (JS → stash → store → admin parse) can't drift on casing.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Whether anything at all is attached — false once the reporter removes both sections, which
    /// is the signal to store null rather than an empty "{}" blob.</summary>
    [JsonIgnore] public bool HasAnything => Diagnostics is not null || PageContent is not null;

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    /// <summary>Parse a stored blob back, tolerating null/blank/garbage: a report predating this feature,
    /// or a corrupt row, yields null rather than throwing where it's rendered.</summary>
    public static BugReportSnapshot? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<BugReportSnapshot>(json, Options); }
        catch (JsonException) { return null; }
    }
}

/// <summary>The environment half of a snapshot: where the reporter was and what their client looked like,
/// plus a bounded buffer of the most recent client-side JS errors (window <c>error</c> /
/// <c>unhandledrejection</c> / <c>console.error</c>) collected over the session. Everything here is a
/// display string — nothing is acted on, so an odd value is at worst noise on the admin's screen.</summary>
public sealed record BugDiagnostics(
    string? Url,
    string? Viewport,
    string? UserAgent,
    string? Theme,
    bool ReducedMotion,
    string? LocalTime,
    string? TimeZone,
    IReadOnlyList<string>? JsErrors);
