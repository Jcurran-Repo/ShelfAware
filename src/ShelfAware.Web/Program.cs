using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Ingest;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Speech;
using ShelfAware.Core.Tagging;
using ShelfAware.Llm;
using ShelfAware.Web.Components;
using ShelfAware.Web.Data;
using ShelfAware.Web.Ingest;
using ShelfAware.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // Voice sends a recorded utterance from the browser to .NET as base64 over the circuit; the 32 KB
    // default is too small for even a few seconds of audio. 4 MB comfortably covers a push-to-talk bark.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024);

// SQLite lives in a configurable data directory: ./app-data locally, /home/data on Azure.
// (Not "data": on case-insensitive filesystems that collides with the Data/ source folder.)
var dataDir = builder.Configuration["DataDir"] ?? Path.Combine(builder.Environment.ContentRootPath, "app-data");
var receiptsDir = Path.Combine(dataDir, "receipts");
Directory.CreateDirectory(receiptsDir);
builder.Services.AddDbContextFactory<ShelfAwareDbContext>(options =>
    // SplitQuery: several read paths Include two+ collections (Purchases + Signals + Tags/Substitutes).
    // As a single query that's a cartesian join — row-multiplying and slow — which is what EF's [20504]
    // startup warning flags. Splitting issues one query per collection instead: no row explosion, warning
    // gone. Fine here because these are read-only display loads (no cross-collection write consistency need).
    options.UseSqlite($"Data Source={Path.Combine(dataDir, "shelfaware.db")}",
        sqlite => sqlite.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));
builder.Services.AddSingleton(new AppPaths(dataDir, receiptsDir));

builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));

// The provider seam: the AI services depend only on IChatClient, so the provider is a swap and the logic
// stays fakeable in tests. Under BYOK each circuit gets its own IChatClient built from that visitor's
// browser-held settings (CircuitAiSettings), so concurrent visitors never share a key; local dev falls
// back to the server config. ByokChatClient builds lazily at call time, so a keyless boot is fine — the
// friendly "add a key" error only surfaces when someone actually makes a call.
builder.Services.AddSingleton<IChatClientFactory, ChatClientFactory>();
builder.Services.AddScoped<CircuitAiSettings>();
builder.Services.AddScoped<IChatClient, ByokChatClient>();

// Per-circuit bus wiring the layout voice agent to the pages (data-changed refresh + resume hand-off).
builder.Services.AddScoped<VoiceCoordinator>();

// The AI services depend (directly or transitively) on the per-circuit IChatClient, so they're scoped —
// a singleton can't hold a scoped dependency. Their code is unchanged. EfPantryStore has no AI dependency
// (just the DbContext factory), so it stays a singleton.
builder.Services.AddScoped<IReceiptExtractor, AnthropicReceiptExtractor>();
builder.Services.AddSingleton<IPantryStore, EfPantryStore>();
builder.Services.AddScoped<IPantryChat, AnthropicPantryChat>();
builder.Services.AddScoped<ITagAdvisor, AnthropicTagAdvisor>();
builder.Services.AddScoped<IRecipeAdvisor, AnthropicRecipeAdvisor>();
builder.Services.AddScoped<IProductSubstituteAdvisor, AnthropicProductSubstituteAdvisor>();
builder.Services.AddScoped<IIngredientAlternativesAdvisor, AnthropicIngredientAlternativesAdvisor>();
builder.Services.AddScoped<IRecipeAdapter, RecipeAdapter>();

// Receipt auto-import: a swappable inbox (local folder now, cloud later) + the importer the chat/voice
// agent triggers, plus the runtime settings store behind the /settings page. Both the importer and the
// manual Upload review confirm receipts through the ONE shared confirmation service.
builder.Services.AddSingleton<IAppSettings, EfAppSettings>();
builder.Services.AddSingleton<IReceiptInbox, LocalFolderReceiptInbox>();
builder.Services.AddScoped<IReceiptImporter, ReceiptImporter>(); // depends on the scoped IReceiptExtractor
builder.Services.AddSingleton<ReceiptConfirmationService>();

// Voice I/O (ElevenLabs): Scribe = STT (ear), TTS = mouth. Speech is its own REST API, not an
// IChatClient workload, so each rides a typed HttpClient with the base address + xi-api-key header.
// Typed clients are transient (the factory owns handler lifetime) — fine, the services are stateless.
builder.Services.Configure<ElevenLabsOptions>(builder.Configuration.GetSection(ElevenLabsOptions.SectionName));
builder.Services.AddHttpClient<ISpeechToText, ElevenLabsSpeechToText>(ConfigureElevenLabs);
builder.Services.AddHttpClient<ITextToSpeech, ElevenLabsTextToSpeech>(ConfigureElevenLabs);

// Per-circuit ElevenLabs credentials: the visitor's own key from their browser (dev falls back to config).
// Scoped, so concurrent visitors never share a voice key; the speech services read it per request.
builder.Services.AddScoped<CircuitVoiceCredentials>();
builder.Services.AddScoped<IVoiceCredentials>(sp => sp.GetRequiredService<CircuitVoiceCredentials>());

static void ConfigureElevenLabs(IServiceProvider sp, HttpClient http)
{
    // Base address only — the xi-api-key is attached PER REQUEST from the visitor's per-circuit credentials
    // (CircuitVoiceCredentials), never baked in as a default header.
    http.BaseAddress = new Uri("https://api.elevenlabs.io");
}

// Rate-limit the cook-along signed-url endpoint per IP, so nobody can spam a visitor's ElevenLabs key
// through it. Built-in ASP.NET Core rate limiting — no package.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("cookalong", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ShelfAwareDbContext>>();
    using var db = factory.CreateDbContext();
    db.Database.EnsureCreated();
    // Lightweight additive "migration": EnsureCreated builds a fresh DB's full schema but never ALTERs
    // an existing one, so columns added after a single-user DB was first created must be backfilled or
    // the app breaks on load. Idempotent — only adds the column when it's missing.
    EnsureColumn(db, "Recipes", "EstimatedCaloriesPerServing", "INTEGER NULL");
    EnsureColumn(db, "Recipes", "ParentRecipeId", "INTEGER NULL");
    EnsureColumn(db, "RecipeIngredients", "AlternativesJson", "TEXT NULL");
    EnsureColumn(db, "Receipts", "SourceFile", "TEXT NULL");
    EnsureColumn(db, "ReceiptLines", "TagsJson", "TEXT NULL");
    EnsureColumn(db, "ReceiptLines", "SuggestedProduct", "TEXT NULL");
    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS \"AppSettings\" (\"Key\" TEXT NOT NULL CONSTRAINT \"PK_AppSettings\" PRIMARY KEY, \"Value\" TEXT NOT NULL);");
    // Additive child table for product substitutes ("also works as"). EnsureCreated builds it on a fresh
    // DB; create it here so an existing single-user DB gets it too. Mirrors the ProductTags schema.
    db.Database.ExecuteSqlRaw(
        "CREATE TABLE IF NOT EXISTS \"ProductSubstitutes\" (" +
        "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_ProductSubstitutes\" PRIMARY KEY AUTOINCREMENT, " +
        "\"ProductId\" INTEGER NOT NULL, \"Value\" TEXT NOT NULL, " +
        "CONSTRAINT \"FK_ProductSubstitutes_Products_ProductId\" FOREIGN KEY (\"ProductId\") REFERENCES \"Products\" (\"Id\") ON DELETE CASCADE);");
    db.Database.ExecuteSqlRaw(
        "CREATE INDEX IF NOT EXISTS \"IX_ProductSubstitutes_ProductId\" ON \"ProductSubstitutes\" (\"ProductId\");");
}

static void EnsureColumn(ShelfAwareDbContext db, string table, string column, string columnDef)
{
    var conn = db.Database.GetDbConnection();
    var wasClosed = conn.State != System.Data.ConnectionState.Open;
    if (wasClosed) conn.Open();
    try
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {columnDef};";
        alter.ExecuteNonQuery();
    }
    finally
    {
        if (wasClosed) conn.Close();
    }
}

// Auto-scan the receipt inbox on startup (in the background; a no-op when no folder is configured), so
// dropped receipts get imported with no manual step — the low-input path.
app.Lifetime.ApplicationStarted.Register(() => _ = ScanReceiptsAsync(app));

static async Task ScanReceiptsAsync(WebApplication app)
{
    // The background startup scan has no browser/circuit, so it can only run on the server-config key —
    // a local-dev or self-hosted owner key. On the public BYOK deploy there's no owner key, so skip it;
    // imports there happen per-visitor via the Settings "Scan now" button, under that visitor's own key.
    var llm = app.Services.GetRequiredService<IOptions<LlmOptions>>().Value;
    if (string.IsNullOrWhiteSpace(llm.ApiKey)) return;
    try
    {
        // IReceiptImporter is scoped now (it depends on the per-circuit AI graph), so resolve it inside a
        // scope. With no browser, CircuitAiSettings keeps its server-config fallback — the owner key.
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IReceiptImporter>().ImportNewAsync();
    }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ReceiptAutoScan").LogError(ex, "Startup receipt auto-scan failed.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Security headers on every response. The CSP is the one that matters for BYOK: the visitor's key lives
// in their browser's localStorage, so the realistic way it leaks is a script exfiltrating it. Restricting
// script-src to our own origin (no arbitrary inline/eval), locking connect-src to only the endpoints we
// actually talk to, denying framing, and dropping the referrer shrink that surface hard. (esm.sh — that
// one origin only — serves the opt-in cook-along SDK at a pinned version; a multi-module ESM SDK can't be
// practically self-hosted without a bundler. media/data: is for the synthesized speech-audio playback.)
// In Development ONLY, loosen exactly two directives so Visual Studio's Browser Link + hot reload work —
// they inject an inline bootstrap script and talk over ephemeral localhost websockets, which the strict
// policy blocks (silently breaking hot reload). Production stays fully locked down.
var cspScriptSrc = app.Environment.IsDevelopment()
    ? "script-src 'self' https://esm.sh 'unsafe-inline'; "
    : "script-src 'self' https://esm.sh; ";
var cspConnectSrc = app.Environment.IsDevelopment()
    ? "connect-src 'self' https://api.elevenlabs.io wss://api.elevenlabs.io ws://localhost:* wss://localhost:* http://localhost:* https://localhost:*; "
    : "connect-src 'self' https://api.elevenlabs.io wss://api.elevenlabs.io; ";
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["Content-Security-Policy"] =
        "default-src 'self'; " +
        cspScriptSrc +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "media-src 'self' data:; " +
        cspConnectSrc +
        "object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    h["Permissions-Policy"] = "microphone=(self), camera=(), geolocation=()";
    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();
app.UseRateLimiter();

app.MapStaticAssets();

// Mints a short-lived ElevenLabs signed URL for the cook-along realtime agent, using the VISITOR's own
// key + agent id (sent from their browser over HTTPS) — the app ships with no voice key of its own. The
// key is used only for this call and is never stored or logged; dev/self-host falls back to server config.
// Rate-limited per IP so nobody can spam a visitor's key through it. A custom header is also a mild CSRF
// guard (cross-site forms can't set one).
app.MapGet("/api/cookalong/signed-url", async (HttpContext ctx, IHttpClientFactory httpFactory, IOptions<ElevenLabsOptions> opts, CancellationToken ct) =>
{
    var apiKey = ctx.Request.Headers["X-EL-Key"].ToString();
    var agentId = ctx.Request.Headers["X-EL-Agent"].ToString();
    if (string.IsNullOrEmpty(apiKey)) apiKey = opts.Value.ApiKey;       // dev / self-host fallback
    if (string.IsNullOrEmpty(agentId)) agentId = opts.Value.AgentId;
    if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(agentId))
        return Results.Problem("Hands-free cook-along needs your ElevenLabs key + agent id in Settings.", statusCode: 503);

    var http = httpFactory.CreateClient();
    using var request = new HttpRequestMessage(HttpMethod.Get,
        $"https://api.elevenlabs.io/v1/convai/conversation/get_signed_url?agent_id={Uri.EscapeDataString(agentId)}");
    request.Headers.Add("xi-api-key", apiKey);

    using var response = await http.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
        return Results.Problem($"Couldn't start the cook-along session ({(int)response.StatusCode}).", statusCode: 502);

    // ElevenLabs returns { "signed_url": "wss://..." }; pass it straight through to the client.
    return Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json");
}).RequireRateLimiting("cookalong");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
