using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using ShelfAware.Llm;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Ingest;

/// <summary>
/// The shared front door for the two photo-upload endpoints (<c>/api/receipts/extract</c> and
/// <c>/api/pantry-photo/read</c>). Both take the visitor's browser-resized photos OFF the SignalR
/// circuit, so both need exactly the same security handling — antiforgery, a body-size ceiling set
/// before a byte is read, per-file/count/type limits, and the BYOK key applied from request headers.
/// Kept in one place so a change to any of those (or a bug in one) can't apply to only one endpoint.
/// </summary>
internal static class PhotoUploadIntake
{
    internal const int MaxFiles = 20;                       // one receipt's pages / a shelf's photos
    internal const long MaxFileBytes = 10L * 1024 * 1024;   // a resized photo is well under 1 MB — the abuse ceiling
    internal const long MaxRequestBytes = 30L * 1024 * 1024;

    /// <summary>One uploaded file, read into memory.</summary>
    internal readonly record struct UploadedFile(byte[] Bytes, string MediaType);

    /// <summary>Validate and read the uploaded files, or return the <see cref="IResult"/> to send back.
    /// Exactly one of the two is non-null. <paramref name="mediaTypeAllowed"/> lets the receipt endpoint
    /// accept PDFs while the census endpoint takes images only; <paramref name="maxFiles"/> lets the census
    /// enforce its own smaller cap (the shelf reader looks at a handful) server-side, not just in the UI.</summary>
    internal static async Task<(List<UploadedFile>? Files, IResult? Error)> ReadAsync(
        HttpRequest request, HttpContext ctx, IAntiforgery antiforgery,
        Func<string, bool> mediaTypeAllowed, string rejectedTypeMessage, CancellationToken ct,
        int maxFiles = MaxFiles)
    {
        // Bound the request body BEFORE anything reads it.
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = MaxRequestBytes;

        // CSRF: the antiforgery token rides the RequestVerificationToken header (paired with the cookie set
        // on page load), read from the header so validation doesn't consume the multipart body.
        try { await antiforgery.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException)
        {
            return (null, Results.Json(new { error = "Your session expired — reload the page and try again." },
                statusCode: StatusCodes.Status400BadRequest));
        }

        if (!request.HasFormContentType)
            return (null, Results.Json(new { error = "Expected a file upload." }, statusCode: StatusCodes.Status400BadRequest));

        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception ex) when (ex is BadHttpRequestException or InvalidDataException)
        {
            return (null, Results.Json(new { error = "That upload was too large." }, statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        var uploaded = form.Files;
        if (uploaded.Count == 0)
            return (null, Results.Json(new { error = "No image was received." }, statusCode: StatusCodes.Status400BadRequest));
        if (uploaded.Count > maxFiles)
            return (null, Results.Json(new { error = $"Please keep it to {maxFiles} at a time." }, statusCode: StatusCodes.Status400BadRequest));

        var files = new List<UploadedFile>(uploaded.Count);
        foreach (var file in uploaded)
        {
            if (file.Length <= 0)
                return (null, Results.Json(new { error = "An empty file was received." }, statusCode: StatusCodes.Status400BadRequest));
            if (file.Length > MaxFileBytes)
                return (null, Results.Json(new { error = "One of those images is too large." }, statusCode: StatusCodes.Status400BadRequest));
            var mediaType = string.IsNullOrWhiteSpace(file.ContentType) ? "image/jpeg" : file.ContentType;
            if (!mediaTypeAllowed(mediaType))
                return (null, Results.Json(new { error = rejectedTypeMessage }, statusCode: StatusCodes.Status400BadRequest));
            using var buffer = new MemoryStream();
            await using (var stream = file.OpenReadStream())
                await stream.CopyToAsync(buffer, ct);
            files.Add(new UploadedFile(buffer.ToArray(), mediaType));
        }
        return (files, null);
    }

    /// <summary>Overlay the visitor's BYOK key from the request headers onto this request's scoped
    /// <see cref="CircuitAiSettings"/>. A NO-OP on a managed deployment (the server key wins), and blank
    /// models/base-url keep the server defaults. A blank key means "use the server/dev fallback" (a keyless
    /// demo visitor gets the same "needs a key" as on the circuit). The key is used only for this call and
    /// is never stored or logged — the cook-along endpoint's contract.</summary>
    internal static void ApplyByok(HttpRequest request, CircuitAiSettings ai)
    {
        var key = request.Headers["X-AI-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key)) return;
        var provider = Enum.TryParse<AiProvider>(request.Headers["X-AI-Provider"].ToString(), ignoreCase: true, out var p)
            ? p : AiProvider.Anthropic;
        ai.Apply(provider, key,
            request.Headers["X-AI-Extraction-Model"].ToString(),
            request.Headers["X-AI-Chat-Model"].ToString(),
            request.Headers["X-AI-Base-Url"].ToString());
    }
}
