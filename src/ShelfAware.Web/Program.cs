using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using System.Text.Json;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Extraction;
using ShelfAware.Core.Ingest;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Core.Speech;
using ShelfAware.Core.Tagging;
using ShelfAware.Llm;
using ShelfAware.Web.Auth;
using ShelfAware.Web.Components;
using ShelfAware.Web.Components.Account;
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

// Tenancy plumbing (v3): the current household comes from the signed-in user's cookie claim (or an
// explicit pin for background work), and IHouseholdDbFactory hands out contexts pre-scoped to it —
// query filters + insert stamping included. Everything that touches pantry data goes through it;
// only the Program.cs bootstrap uses the raw factory.
builder.Services.AddScoped<ICurrentHousehold, CurrentHousehold>();
builder.Services.AddScoped<IHouseholdDbFactory, HouseholdDbFactory>();

// ---- Authentication & households (v3) ----
// Identity + households live in their OWN SQLite file: a fresh auth.db gets its full schema from
// EnsureCreated on every deployment (the no-migrations rule), and the pantry context stays free of
// Identity noise. Pantry rows reference households by plain id — no cross-file FK.
builder.Services.AddDbContextFactory<AuthDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataDir, "auth.db")}"));

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
// Live circuits re-check the security stamp every 5 minutes, so a logout (which bumps the stamp)
// kills every other tab/device within one interval — not just the browser that clicked Sign out.
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
}).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/Login";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
    // HttpOnly + SameSite=Lax are the defaults; Secure is enforced in production (the tailnet/Azure
    // deploys are HTTPS), relaxed only for the plain-HTTP localhost dev server.
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    // API callers get a plain 401/403 instead of the human login-page redirect.
    options.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        else
        {
            ctx.Response.Redirect(ctx.RedirectUri);
        }
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
        else
        {
            ctx.Response.Redirect(ctx.RedirectUri);
        }
        return Task.CompletedTask;
    };
});

builder.Services.AddIdentityCore<AppUser>(options =>
{
    // No email infrastructure yet (deliberate — see CLAUDE.md), so nothing to confirm.
    options.SignIn.RequireConfirmedAccount = false;
    options.User.RequireUniqueEmail = true;
    // Length beats composition rules (NIST 800-63B): 10+ characters, no forced symbol soup.
    options.Password.RequiredLength = 10;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireDigit = false;
})
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<HouseholdClaimsPrincipalFactory>();

builder.Services.AddAuthorization();
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddScoped<HouseholdService>();

// Auth cookies + antiforgery tokens are encrypted with DataProtection keys. Persist them next to the
// DBs (app-data is gitignored and survives republish) — otherwise every restart/redeploy would sign
// the whole household out and invalidate in-flight forms.
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("ShelfAware");
if (OperatingSystem.IsWindows())
{
    // Encrypt the key ring at rest with the Windows user's DPAPI (the tailnet self-host). On Linux
    // (Azure) the keys stay plain files under app-data — same trust boundary as the SQLite DBs.
    dataProtection.ProtectKeysWithDpapi();
}

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
// a singleton can't hold a scoped dependency. Since v3 the data services are scoped too: they read
// through IHouseholdDbFactory, which needs the scope's signed-in user to know whose pantry it is.
builder.Services.AddScoped<IReceiptExtractor, AnthropicReceiptExtractor>();
builder.Services.AddScoped<IPantryStore, EfPantryStore>();
builder.Services.AddScoped<IPantryChat, AnthropicPantryChat>();
builder.Services.AddScoped<ITagAdvisor, AnthropicTagAdvisor>();
builder.Services.AddScoped<IRecipeAdvisor, AnthropicRecipeAdvisor>();
builder.Services.AddScoped<IProductSubstituteAdvisor, AnthropicProductSubstituteAdvisor>();
builder.Services.AddScoped<IIngredientAlternativesAdvisor, AnthropicIngredientAlternativesAdvisor>();
builder.Services.AddScoped<IRecipeAdapter, RecipeAdapter>();

// Receipt auto-import: a swappable inbox (local folder now, cloud later) + the importer the chat/voice
// agent triggers, plus the runtime settings store behind the /settings page. Both the importer and the
// manual Upload review confirm receipts through the ONE shared confirmation service.
builder.Services.AddScoped<IAppSettings, EfAppSettings>();          // settings are per household now
builder.Services.AddScoped<IReceiptInbox, LocalFolderReceiptInbox>(); // reads the household's folder setting
builder.Services.AddScoped<IReceiptImporter, ReceiptImporter>(); // depends on the scoped IReceiptExtractor
builder.Services.AddScoped<ReceiptConfirmationService>();
builder.Services.AddScoped<ProductRenameService>(); // rename + re-point the name-keyed recipe links
builder.Services.AddScoped<DemoDataSeeder>(); // synthetic demo catalog (guarded: this household's pantry is empty)
builder.Services.AddScoped<UserDataService>();   // export + delete-my-data (one place for both)

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
    // Credential endpoints get a per-IP brake on top of Identity's per-account lockout: lockout
    // protects one account from many guesses, this protects all accounts from one hammering IP.
    // Razor-component form posts aren't attachable endpoints for a named policy, so the global
    // limiter matches them by path; everything else passes through unlimited.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/Account")
            ? RateLimitPartition.GetFixedWindowLimiter(
                "account:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
            : RateLimitPartition.GetNoLimiter("unlimited"));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // Accounts + households (auth.db) — always a from-scratch EnsureCreated (the file is new per
    // deployment site; v3 shipped with no upgrade path for pre-auth pantry DBs — see CLAUDE.md).
    var authFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
    using (var authDb = authFactory.CreateDbContext())
    {
        authDb.Database.EnsureCreated();
    }

    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ShelfAwareDbContext>>();
    using var db = factory.CreateDbContext();
    // v3's breaking schema change: no in-place upgrade for pre-household DBs — fail fast with
    // instructions instead of a confusing "no such column" on the first query.
    PantryDbGuard.ThrowIfPreHouseholdDb(db);
    db.Database.EnsureCreated();
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

// Behind a TLS-terminating reverse proxy (Tailscale Serve for the private self-host, Azure later), honor
// X-Forwarded-Proto/-For from the loopback proxy so HTTPS redirect, HSTS, and per-IP rate limiting see the
// real scheme and client rather than the proxy's localhost hop. Defaults trust only loopback proxies.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

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

// Order matters: antiforgery tokens are bound to the signed-in user, so authentication must have
// resolved the principal before UseAntiforgery sees the request.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();

app.MapStaticAssets();

// Mints a short-lived ElevenLabs signed URL for the cook-along realtime agent, using the VISITOR's own
// key + agent id (sent from their browser over HTTPS) — the app ships with no voice key of its own. The
// key is used only for this call and is never stored or logged; dev/self-host falls back to server config.
// Rate-limited per IP so nobody can spam a visitor's key through it. A custom header is also a mild CSRF
// guard (cross-site forms can't set one).
app.MapGet("/api/cookalong/signed-url", async (HttpContext ctx, IHttpClientFactory httpFactory, IOptions<ElevenLabsOptions> opts, IOptions<LlmOptions> deployment, CancellationToken ct) =>
{
    // Managed deployment: the host's voice key is authoritative — ignore any header a browser sends.
    var managed = deployment.Value.IsManaged;
    var apiKey = managed ? opts.Value.ApiKey : ctx.Request.Headers["X-EL-Key"].ToString();
    var agentId = managed ? opts.Value.AgentId : ctx.Request.Headers["X-EL-Agent"].ToString();
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
}).RequireRateLimiting("cookalong").RequireAuthorization();

// Full data export ("Download my data") — a portable JSON snapshot; also the "export first" offered
// before Delete my data. Reads the user's own content only (no keys, no app config).
app.MapGet("/api/data/export", async (UserDataService data, CancellationToken ct) =>
{
    var snapshot = await data.ExportAsync(ct);
    var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { WriteIndented = true });
    return Results.File(json, "application/json", $"shelfaware-data-{DateTime.Now:yyyy-MM-dd}.json");
}).RequireAuthorization();

// PWA manifest — makes the app installable ("Add to home screen"). Served explicitly so the content type
// is right regardless of static-file MIME config; it loads under the same-origin CSP (manifest-src falls
// back to default-src 'self'). No service worker: this is a server-rendered app, so there's no offline mode.
app.MapGet("/manifest.webmanifest", () => Results.Content("""
{
  "name": "Shelf Aware",
  "short_name": "ShelfAware",
  "description": "Know what you're running low on before you run out.",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "background_color": "#f6f7f9",
  "theme_color": "#2563eb",
  "icons": [
    { "src": "/icons/icon-192.png", "sizes": "192x192", "type": "image/png", "purpose": "any" },
    { "src": "/icons/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any" },
    { "src": "/icons/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ]
}
""", "application/manifest+json"));

app.MapRazorComponents<App>()
    // The framework appends its own "frame-ancestors 'self'" CSP (its clickjacking mitigation for
    // compressed WebSockets), comma-joining a SECOND policy onto the strict one our security-headers
    // middleware already sends. Ours says frame-ancestors 'none' on every response — strictly
    // stronger — so suppress the framework copy for one clean policy. Compression stays enabled.
    .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null);

// The logout POST (auth cookies can't be cleared over a circuit).
app.MapAdditionalIdentityEndpoints();

app.Run();
