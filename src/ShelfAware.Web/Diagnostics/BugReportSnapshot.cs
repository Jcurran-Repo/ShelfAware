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

    // Server-enforced upper bounds on the stored blob. The caps in bug-capture.js are the BROWSER-enforced
    // half only — the snapshot arrives as a JS→.NET interop return, and a signed-in user can redefine
    // window.shelfawareBugCapture.snapshot in their console to return arbitrary sizes (bounded only by the
    // 4 MB circuit message limit). So the server clamps too, the same way the /bugs form clamps Body and
    // PageUrl. Generously above the JS output (content ~8 KB, 20 errors) so a legitimate capture is never
    // clipped, while a tampered one can't grow the shared DB without bound. Char-based (not byte-based):
    // the goal is a bounded row, not an exact size.
    private const int MaxPageContent = 10_000;
    private const int MaxField = 512;       // url, viewport, theme, userAgent, localTime, timeZone
    private const int MaxErrors = 30;
    private const int MaxErrorLength = 512;

    /// <summary>Whether anything at all is attached — false once the reporter removes both sections, which
    /// is the signal to store null rather than an empty "{}" blob.</summary>
    [JsonIgnore] public bool HasAnything => Diagnostics is not null || PageContent is not null;

    /// <summary>A copy with every field clamped to the server-side bounds above — call before
    /// <see cref="Serialize"/> on the store path. Clamping the SEGMENTS (not the serialized string) keeps
    /// the JSON valid; truncating the serialized blob would risk a mid-string cut that <see cref="TryParse"/>
    /// then reads back as null, silently losing the whole snapshot.</summary>
    public BugReportSnapshot Bounded() => new(
        Diagnostics is null ? null : Diagnostics with
        {
            Url = Cap(Diagnostics.Url, MaxField),
            Viewport = Cap(Diagnostics.Viewport, MaxField),
            UserAgent = Cap(Diagnostics.UserAgent, MaxField),
            Theme = Cap(Diagnostics.Theme, MaxField),
            LocalTime = Cap(Diagnostics.LocalTime, MaxField),
            TimeZone = Cap(Diagnostics.TimeZone, MaxField),
            JsErrors = Diagnostics.JsErrors?.Take(MaxErrors).Select(e => Cap(e, MaxErrorLength)!).ToList(),
        },
        Cap(PageContent, MaxPageContent));

    private static string? Cap(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

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
/// display string — nothing is acted on, so an odd value is at worst noise on the admin's screen.
/// ⚠️ The <see cref="JsErrors"/> list gives this record REFERENCE equality (auto-generated record
/// <c>Equals</c> uses the list's default comparer, not element-wise), so two value-equal snapshots compare
/// unequal — assert on fields, never on whole-record equality.</summary>
public sealed record BugDiagnostics(
    string? Url,
    string? Viewport,
    string? UserAgent,
    string? Theme,
    bool ReducedMotion,
    string? LocalTime,
    string? TimeZone,
    IReadOnlyList<string>? JsErrors);
