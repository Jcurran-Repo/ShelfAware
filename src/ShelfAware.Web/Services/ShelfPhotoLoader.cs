using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Census;
using ShelfAware.Llm;

namespace ShelfAware.Web.Services;

/// <summary>
/// Turns a photo the visitor picked into one the census reader can look at: downscaled in the BROWSER
/// before it ever crosses the circuit, exactly as receipt images are (DESIGN.md §5, <c>Llm:MaxImageEdgePx</c>).
/// <para>An interface because this is a browser seam, and the page-test harness fakes browser seams by
/// policy — <c>IBrowserFile.RequestImageFileAsync</c> reaches into JS and cannot run under bUnit at all, so
/// without a seam here the entire review grid, its tick defaults, its product pre-fill and its confirm
/// would be reachable only by hand. One interface with two implementations from the first day, not a
/// speculative abstraction.</para>
/// </summary>
public interface IShelfPhotoLoader
{
    Task<ShelfPhoto> LoadAsync(IBrowserFile file, CancellationToken cancellationToken = default);
}

public sealed class BrowserShelfPhotoLoader(IOptions<LlmOptions> options) : IShelfPhotoLoader
{
    /// <summary>A backstop on the DOWNSCALED image, which is the only thing that crosses the circuit — the
    /// browser resize below is what actually bounds what reaches the model, and at a 1568px longest edge
    /// the JPEG is comfortably under a megabyte. So this can only fire if the resize returned something
    /// unexpected; it is not a limit on the file the visitor picked (that one is read and shrunk entirely
    /// in their browser and never arrives here whole).</summary>
    private const long MaxUploadBytes = 25 * 1024 * 1024;

    /// <summary>The formats the browser is asked to decode, matching the file input's accept list. ⚠️ That
    /// attribute is a hint the picker's "All Files" filter and drag-and-drop both walk straight past, and
    /// anything the browser can't decode into an <c>&lt;img&gt;</c> does NOT fail — Blazor's own
    /// <c>toImageFile</c> revokes the object URL on error and never rejects the promise, so an unreadable
    /// file spends the full timeout below looking like a slow photo. Refusing it by content type up front
    /// turns a 30-second silence into an instant sentence that names the problem. The receipt page makes
    /// the same check the other way round (it branches PDFs out before resizing).</summary>
    private static readonly string[] Decodable = ["image/jpeg", "image/png", "image/gif", "image/webp"];

    public async Task<ShelfPhoto> LoadAsync(IBrowserFile file, CancellationToken cancellationToken = default)
    {
        if (!Decodable.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"“{file.Name}” is {(string.IsNullOrWhiteSpace(file.ContentType) ? "an unknown type" : file.ContentType)}, "
                + "which can't be read as a photo. Use a JPEG, PNG, GIF, or WebP.");
        }

        var maxEdge = options.Value.MaxImageEdgePx;
        // Blazor's JS never settles this promise when the image fails to load in the browser (seen live on
        // the receipt path: a CSP without img-src blob: blocked it and the upload hung on the spinner
        // forever). Bounded, so a browser-side failure becomes this photo's normal error instead of a hang.
        var resized = await file.RequestImageFileAsync("image/jpeg", maxEdge, maxEdge)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        // `await using` on the stream, not just the buffer: disposing a browser file stream is what
        // releases the JS-side stream reference, and the class has no finalizer — an undisposed one is
        // never reclaimed for the life of the page, times eight photos per census.
        using var buffer = new MemoryStream();
        await using (var source = resized.OpenReadStream(MaxUploadBytes, cancellationToken))
        {
            await source.CopyToAsync(buffer, cancellationToken);
        }
        return new ShelfPhoto(buffer.ToArray(), "image/jpeg");
    }
}
