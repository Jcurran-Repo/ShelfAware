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

    public async Task<ShelfPhoto> LoadAsync(IBrowserFile file, CancellationToken cancellationToken = default)
    {
        var maxEdge = options.Value.MaxImageEdgePx;
        // Blazor's JS never settles this promise when the image fails to load in the browser (seen live on
        // the receipt path: a CSP without img-src blob: blocked it and the upload hung on the spinner
        // forever). Bounded, so a browser-side failure becomes this photo's normal error instead of a hang.
        var resized = await file.RequestImageFileAsync("image/jpeg", maxEdge, maxEdge)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        using var buffer = new MemoryStream();
        await resized.OpenReadStream(MaxUploadBytes, cancellationToken).CopyToAsync(buffer, cancellationToken);
        return new ShelfPhoto(buffer.ToArray(), "image/jpeg");
    }
}
