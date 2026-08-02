using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Options;
using ShelfAware.Llm;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The browser half of the census intake. Only the guard that runs BEFORE interop is testable here — the
/// resize itself is a JS call — but that guard is the whole reason this class refuses anything at all:
/// Blazor's <c>toImageFile</c> revokes the object URL on a decode error and never rejects the promise, so
/// a file the browser can't turn into an image does not fail. It hangs, until the timeout, looking exactly
/// like a slow photo.
/// </summary>
public class ShelfPhotoLoaderTests
{
    private static BrowserShelfPhotoLoader Loader() =>
        new(Options.Create(new LlmOptions { MaxImageEdgePx = 1568 }));

    private sealed class FakeBrowserFile(string name, string contentType) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.Now;
        public long Size => 1024;
        public string ContentType => contentType;

        // Never actually reached: RequestImageFileAsync is an extension that rejects any IBrowserFile
        // that isn't Blazor's own internal type, so LoadAsync stops one step earlier. That is precisely
        // why these tests assert on WHICH exception comes back rather than on success — getting past the
        // guard is observable, completing the resize is not.
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("unreachable under this fake — see the note above");
    }

    [Theory]
    [InlineData("application/pdf")]   // the receipt page's input accepts these, so people will try one here
    [InlineData("application/zip")]
    [InlineData("text/plain")]
    [InlineData("video/mp4")]         // a picker set to "All Files" happily offers these
    public async Task A_file_that_is_plainly_not_an_image_is_refused_by_name_not_left_to_time_out(string contentType)
    {
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            Loader().LoadAsync(new FakeBrowserFile("shelf.dat", contentType)));

        // The message is shown to the visitor verbatim, so it has to name the file — the generic
        // "something went wrong" it replaces never said the file was the problem.
        Assert.Contains("shelf.dat", ex.Message);
        Assert.Contains(contentType, ex.Message);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("IMAGE/JPEG")]  // browsers are not required to lower-case it
    [InlineData("image/heic")]  // ⚠️ a shelf photo picked through the iOS Files app; WebKit decodes it
    [InlineData("image/avif")]
    [InlineData("image/bmp")]
    [InlineData("image/tiff")]
    [InlineData("")]            // no extension, so the OS supplied no type — not a reason to refuse a photo
    public async Task Anything_image_shaped_reaches_the_resize_rather_than_being_refused(string contentType)
    {
        // ⚠️ The half that matters, because the first version of this guard was an allowlist of four
        // formats — narrower than the thing it guards. Blazor's toImageFile never inspects the MIME type;
        // it paints the file into an <img> and re-encodes through a canvas, so it decodes whatever the
        // BROWSER can. The allowlist refused real photos. Anything image-shaped that still can't be
        // decoded falls through to the 30s timeout, which is the honest place for it.
        var thrown = await Record.ExceptionAsync(() =>
            Loader().LoadAsync(new FakeBrowserFile("shelf.img", contentType)));

        Assert.IsNotType<NotSupportedException>(thrown);
    }
}
