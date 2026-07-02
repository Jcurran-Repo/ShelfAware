using Anthropic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Speech;
using ShelfAware.Core.Tagging;
using ShelfAware.Llm;
using ShelfAware.Web.Components;
using ShelfAware.Web.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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

builder.Services.AddSingleton<IReceiptExtractor, AnthropicReceiptExtractor>();
builder.Services.AddSingleton<IPantryStore, EfPantryStore>();
builder.Services.AddSingleton<IPantryChat, AnthropicPantryChat>();
builder.Services.AddSingleton<ITagAdvisor, AnthropicTagAdvisor>();
builder.Services.AddSingleton<IRecipeAdvisor, AnthropicRecipeAdvisor>();

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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
