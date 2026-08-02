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

    /// <summary>Refuse only what is plainly NOT an image. ⚠️ Deliberately a prefix test and not an
    /// allowlist of formats: Blazor's <c>toImageFile</c> never inspects the MIME type — it paints the file
    /// into an <c>&lt;img&gt;</c> and re-encodes through a canvas — so it decodes whatever the BROWSER
    /// can, which includes HEIC on WebKit, AVIF, BMP and TIFF. A four-format allowlist was narrower than
    /// the thing it guards and refused real photos: iOS transcodes HEIC only when the file input's accept
    /// list asks for JPEG, so the Photos path is safe but a shelf photo picked through the Files app
    /// arrives as image/heic and Safari can read it perfectly well.
    /// <para>The guard still earns its place, because a file the browser CAN'T decode does not fail —
    /// <c>toImageFile</c>'s error handler revokes the object URL and never settles the promise, so a PDF
    /// spends the full timeout below looking like a slow photo. Catching the obvious cases by prefix turns
    /// that silence into an instant sentence; anything image-shaped that still can't be decoded falls
    /// through to the timeout, which is the honest place for it.</para>
    /// <para>An EMPTY content type is let through on purpose — the OS picker supplies nothing for a file
    /// with no extension, and refusing those would block a photo straight off a camera to spare it a
    /// timeout it will probably never hit.</para>
    /// ⚠️ Do NOT add image/heic to the page's accept list to "support" it: Safari 17+ then stops
    /// transcoding and converts JPEG and PNG INTO HEIC, making every iPhone upload the rare case.</summary>
    private static bool LooksLikeAnImage(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType)
        || contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public async Task<ShelfPhoto> LoadAsync(IBrowserFile file, CancellationToken cancellationToken = default)
    {
        if (!LooksLikeAnImage(file.ContentType))
        {
            throw new NotSupportedException(
                $"“{file.Name}” is {file.ContentType}, which isn't a photo. "
                + "Take or pick a picture of the shelf instead.");
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
