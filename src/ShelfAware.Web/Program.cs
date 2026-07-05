using Anthropic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
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
    options.UseSqlite($"Data Source={Path.Combine(dataDir, "shelfaware.db")}"));
builder.Services.AddSingleton(new AppPaths(dataDir, receiptsDir));

builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));

// One IChatClient over the Anthropic SDK's built-in Microsoft.Extensions.AI adapter. The AI services
// depend on this abstraction (not the raw SDK), so the provider is a DI swap and the logic is fakeable
// in tests. Model + token limits are set per call via ChatOptions.
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    return new AnthropicClient { ApiKey = opts.ApiKey }.AsIChatClient(opts.ChatModel);
});

// Per-circuit bus wiring the layout voice agent to the pages (data-changed refresh + resume hand-off).
builder.Services.AddScoped<VoiceCoordinator>();

builder.Services.AddSingleton<IReceiptExtractor, AnthropicReceiptExtractor>();
builder.Services.AddSingleton<IPantryStore, EfPantryStore>();
builder.Services.AddSingleton<IPantryChat, AnthropicPantryChat>();
builder.Services.AddSingleton<ITagAdvisor, AnthropicTagAdvisor>();
builder.Services.AddSingleton<IRecipeAdvisor, AnthropicRecipeAdvisor>();
builder.Services.AddSingleton<IProductSubstituteAdvisor, AnthropicProductSubstituteAdvisor>();
builder.Services.AddSingleton<IIngredientAlternativesAdvisor, AnthropicIngredientAlternativesAdvisor>();
// Adapt-a-recipe-to-what-you-have; singleton so the singleton IPantryChat can inject it (uses the
// DbContext factory + advisor, both singleton-safe).
builder.Services.AddSingleton<IRecipeAdapter, RecipeAdapter>();

// Receipt auto-import: a swappable inbox (local folder now, cloud later) + the importer the chat/voice
// agent triggers, plus the runtime settings store behind the /settings page. Both the importer and the
// manual Upload review confirm receipts through the ONE shared confirmation service.
builder.Services.AddSingleton<IAppSettings, EfAppSettings>();
builder.Services.AddSingleton<IReceiptInbox, LocalFolderReceiptInbox>();
builder.Services.AddSingleton<IReceiptImporter, ReceiptImporter>();
builder.Services.AddSingleton<ReceiptConfirmationService>();

// Voice I/O (ElevenLabs): Scribe = STT (ear), TTS = mouth. Speech is its own REST API, not an
// IChatClient workload, so each rides a typed HttpClient with the base address + xi-api-key header.
// Typed clients are transient (the factory owns handler lifetime) — fine, the services are stateless.
builder.Services.Configure<ElevenLabsOptions>(builder.Configuration.GetSection(ElevenLabsOptions.SectionName));
builder.Services.AddHttpClient<ISpeechToText, ElevenLabsSpeechToText>(ConfigureElevenLabs);
builder.Services.AddHttpClient<ITextToSpeech, ElevenLabsTextToSpeech>(ConfigureElevenLabs);

static void ConfigureElevenLabs(IServiceProvider sp, HttpClient http)
{
    var opts = sp.GetRequiredService<IOptions<ElevenLabsOptions>>().Value;
    http.BaseAddress = new Uri("https://api.elevenlabs.io");
    // No key yet? Leave the header off and let the call 401 gracefully — the app still boots.
    if (!string.IsNullOrEmpty(opts.ApiKey))
        http.DefaultRequestHeaders.Add("xi-api-key", opts.ApiKey);
}

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
    try
    {
        await app.Services.GetRequiredService<IReceiptImporter>().ImportNewAsync();
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
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// Mints a short-lived signed URL so the browser can open a realtime ElevenLabs cook-along session
// WITHOUT ever seeing the API key (the key stays here on the server). The agent handles turn-taking +
// barge-in; the recipe is injected client-side as a dynamic variable.
app.MapGet("/api/cookalong/signed-url", async (IHttpClientFactory httpFactory, IOptions<ElevenLabsOptions> opts, CancellationToken ct) =>
{
    var o = opts.Value;
    if (string.IsNullOrEmpty(o.ApiKey) || string.IsNullOrEmpty(o.AgentId))
        return Results.Problem("Hands-free cook-along isn't configured (needs ElevenLabs key + agent id).", statusCode: 503);

    var http = httpFactory.CreateClient();
    using var request = new HttpRequestMessage(HttpMethod.Get,
        $"https://api.elevenlabs.io/v1/convai/conversation/get_signed_url?agent_id={Uri.EscapeDataString(o.AgentId)}");
    request.Headers.Add("xi-api-key", o.ApiKey);

    using var response = await http.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
        return Results.Problem($"Couldn't start the cook-along session ({(int)response.StatusCode}).", statusCode: 502);

    // ElevenLabs returns { "signed_url": "wss://..." }; pass it straight through to the client.
    return Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
