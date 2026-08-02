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

        // Reaching either of these means the guard let the file through — which on a real circuit is the
        // 30-second hang, so failing loudly here is the point.
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the loader should have refused this file before reading it");
    }

    [Theory]
    [InlineData("application/pdf")]      // the receipt page's own input accepts these, so people will try
    [InlineData("image/heic")]           // an iPhone photo picked through the Files app
    [InlineData("application/zip")]
    [InlineData("")]                     // the OS dialog's "All Files" filter often supplies nothing
    public async Task A_file_the_browser_cannot_decode_is_refused_by_name_not_left_to_time_out(string contentType)
    {
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            Loader().LoadAsync(new FakeBrowserFile("shelf.dat", contentType)));

        // The message has to name the file and the way out: it is shown to the visitor verbatim, and the
        // generic "something went wrong" it replaces never mentioned that the file was the problem.
        Assert.Contains("shelf.dat", ex.Message);
        Assert.Contains("JPEG", ex.Message);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    [InlineData("IMAGE/JPEG")] // browsers are not required to lower-case it
    public async Task Every_format_the_file_input_accepts_gets_past_the_guard(string contentType)
    {
        // The complement, and the one that keeps the guard from quietly becoming a wall: each of these
        // must reach the resize. It throws from the fake stream — which is proof it got there, since the
        // only other way out of LoadAsync is the refusal above.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Loader().LoadAsync(new FakeBrowserFile("shelf.jpg", contentType)));

        var refusal = await Record.ExceptionAsync(() =>
            Loader().LoadAsync(new FakeBrowserFile("shelf.jpg", contentType)));
        Assert.IsNotType<NotSupportedException>(refusal);
    }
}
