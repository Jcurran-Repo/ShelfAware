using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ShelfAware.Llm;
using ShelfAware.Web.Ingest;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.UI.Tests;

/// <summary>
/// The shared front door both photo-upload endpoints use (<c>/api/receipts/extract</c>,
/// <c>/api/pantry-photo/read</c>). It's plain and security-relevant — antiforgery, the body-size cap, the
/// per-file/count/type limits, the BYOK key from headers — so its REJECTION paths are pinned here directly
/// (the page tests mock the JS uploader and only ever exercise the success path). Unit-tested over a
/// DefaultHttpContext; no WebApplicationFactory needed.
/// </summary>
public class PhotoUploadIntakeTests
{
    private static readonly Func<string, bool> ImagesOnly =
        mt => mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private sealed class FakeAntiforgery(bool valid) : IAntiforgery
    {
        public Task ValidateRequestAsync(HttpContext context) =>
            valid ? Task.CompletedTask : throw new AntiforgeryValidationException("bad token");

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext c) => throw new NotSupportedException();
        public AntiforgeryTokenSet GetTokens(HttpContext c) => throw new NotSupportedException();
        public Task<bool> IsRequestValidAsync(HttpContext c) => throw new NotSupportedException();
        public void SetCookieTokenAndHeader(HttpContext c) => throw new NotSupportedException();
    }

    private static IFormFile File(byte[] bytes, string contentType, string name = "photo.jpg") =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name)
        {
            Headers = new HeaderDictionary { ["Content-Type"] = contentType },
        };

    private static DefaultHttpContext ContextWith(bool asForm = true, params IFormFile[] files)
    {
        var ctx = new DefaultHttpContext();
        if (asForm)
        {
            ctx.Request.ContentType = "multipart/form-data; boundary=x";
            var collection = new FormFileCollection();
            collection.AddRange(files);
            ctx.Request.Form = new FormCollection(new(), collection);
        }
        return ctx;
    }

    private static Task<(List<PhotoUploadIntake.UploadedFile>? Files, IResult? Error)> Read(
        DefaultHttpContext ctx, bool validToken = true, int maxFiles = PhotoUploadIntake.MaxFiles) =>
        PhotoUploadIntake.ReadAsync(ctx.Request, ctx, new FakeAntiforgery(validToken), ImagesOnly,
            "Only photos are accepted.", CancellationToken.None, maxFiles);

    [Fact]
    public async Task An_invalid_antiforgery_token_is_refused()
    {
        var (files, error) = await Read(ContextWith(files: File([1], "image/jpeg")), validToken: false);
        Assert.Null(files);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task A_non_form_request_is_refused()
    {
        var (files, error) = await Read(ContextWith(asForm: false));
        Assert.Null(files);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Zero_files_is_refused()
    {
        var (files, error) = await Read(ContextWith());
        Assert.Null(files);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var (files, error) = await Read(ContextWith(files: File([], "image/jpeg")));
        Assert.Null(files);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task An_oversized_file_is_refused()
    {
        var tooBig = new byte[PhotoUploadIntake.MaxFileBytes + 1];
        var (files, error) = await Read(ContextWith(files: File(tooBig, "image/jpeg")));
        Assert.Null(files);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task A_disallowed_media_type_is_refused()
    {
        // ImagesOnly (the census predicate) rejects a PDF — the receipt endpoint would accept it.
        var (files, error) = await Read(ContextWith(files: File([1, 2, 3], "application/pdf")));
        Assert.Null(files);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task More_than_the_cap_is_refused_at_the_caller_supplied_limit()
    {
        // The census passes maxFiles: 8 — nine photos are refused server-side, not just in the UI.
        var nine = Enumerable.Range(0, 9).Select(_ => File([1], "image/jpeg")).ToArray();
        var (files, error) = await Read(ContextWith(files: nine), maxFiles: 8);
        Assert.Null(files);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Valid_files_are_read_into_memory_with_their_media_types()
    {
        var (files, error) = await Read(ContextWith(
            files: [File([1, 2, 3], "image/jpeg", "a.jpg"), File([4, 5], "image/png", "b.png")]));

        Assert.Null(error);
        Assert.NotNull(files);
        Assert.Equal(2, files!.Count);
        Assert.Equal([1, 2, 3], files[0].Bytes);
        Assert.Equal("image/jpeg", files[0].MediaType);
        Assert.Equal("image/png", files[1].MediaType);
    }

    // --- BYOK header application ---------------------------------------------

    private static CircuitAiSettings Settings(string keyMode, string serverKey = "") =>
        new(Options.Create(new LlmOptions { KeyMode = keyMode, ApiKey = serverKey }));

    [Fact]
    public void The_byok_key_from_headers_is_applied_on_a_byok_deployment()
    {
        var ai = Settings("byok");
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-AI-Key"] = "visitor-key";
        ctx.Request.Headers["X-AI-Provider"] = "OpenAI";

        PhotoUploadIntake.ApplyByok(ctx.Request, ai);

        Assert.Equal("visitor-key", ai.ApiKey);
        Assert.Equal(AiProvider.OpenAI, ai.Provider);
    }

    [Fact]
    public void The_byok_header_is_ignored_on_a_managed_deployment()
    {
        // ⚠️ The load-bearing guard: a header must NOT override the server key (or dodge metering) on a
        // managed box. CircuitAiSettings.Apply is a no-op there, so the server key stands.
        var ai = Settings("managed", serverKey: "server-key");
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-AI-Key"] = "visitor-key";

        PhotoUploadIntake.ApplyByok(ctx.Request, ai);

        Assert.Equal("server-key", ai.ApiKey); // the header did not take over
    }

    [Fact]
    public void A_blank_byok_key_leaves_the_server_or_dev_fallback_in_place()
    {
        var ai = Settings("byok", serverKey: "dev-fallback");
        var ctx = new DefaultHttpContext(); // no X-AI-Key header

        PhotoUploadIntake.ApplyByok(ctx.Request, ai);

        Assert.Equal("dev-fallback", ai.ApiKey);
    }
}
