namespace ShelfAware.Web.Data;

/// <summary>
/// The receipt formats we accept, and the extension ↔ media-type mapping between them. One map: this
/// was previously spelled out three times (the upload page, the self-eval, the folder inbox), which is
/// three chances for them to disagree about what a ".webp" is.
/// </summary>
public static class ReceiptMediaTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".pdf"] = "application/pdf",
    };

    /// <summary>Whether a file is a format we can read at all — the inbox's filter.</summary>
    public static bool IsSupported(string path) => ByExtension.ContainsKey(Path.GetExtension(path));

    /// <summary>The media type for a stored file. Unknown extensions fall back to JPEG: these are our own
    /// saved pages, written with an extension we chose, so an unrecognised one means something is off —
    /// and the extractor coping with a mislabelled image beats refusing to read the receipt.</summary>
    public static string ForPath(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var mediaType) ? mediaType : "image/jpeg";

    /// <summary>The extension to save a page under, without the dot.</summary>
    public static string ExtensionFor(string mediaType) => mediaType switch
    {
        "image/png" => "png",
        "image/gif" => "gif",
        "image/webp" => "webp",
        "application/pdf" => "pdf",
        _ => "jpg",
    };
}
