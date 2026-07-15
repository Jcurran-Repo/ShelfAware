using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
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

// Synthesized speech, filed per household (see CachingTextToSpeech). Resolved here beside the other data
// paths because two things need it: the speech registration, and delete-my-data — a household's audio is
// a recording of its recipes, so wiping the rows and leaving the clips would make that button a lie.
// Speech:CacheMegabytes <= 0 means OFF — the cache isn't registered at all, rather than being emptied at
// every boot while it refills all session (which would re-buy every recipe after a restart AND use the disk).
var speechCacheMb = builder.Configuration.GetValue<int?>("Speech:CacheMegabytes") ?? 256;
var speechCacheDir = speechCacheMb > 0 ? Path.Combine(dataDir, "tts-cache") : null;
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

var authentication = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
});
authentication.AddIdentityCookies();

// External login is CONFIG-GATED: registered only when a Google client id is present, so an
// unconfigured deployment has zero OAuth surface (no button, no endpoints that go anywhere).
// Google asserts the email, so no confirmation step is needed even without an email sender.
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
if (!string.IsNullOrWhiteSpace(googleClientId))
{
    authentication.AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? "";
        options.SignInScheme = IdentityConstants.ExternalScheme;
    });
}
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

// How fast a security-stamp change bites on plain HTTP requests. Identity's default is 30 MINUTES,
// which was survivable when the stamp only changed on logout, but not now that removing a member relies
// on it: the household id rides in the cookie, so a removed member's requests would keep working — and
// keep reading the pantry — for half an hour after they were removed. Five minutes matches
// IdentityRevalidatingAuthenticationStateProvider's circuit interval, so "within a few minutes" is one
// promise rather than two different ones depending on whether you're on a page or hitting an endpoint.
// Cost is a user lookup per user per 5 minutes, which at household scale is nothing.
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromMinutes(5));

builder.Services.AddAuthorization();
// Validated at STARTUP, not trusted. A lifetime of 0 or negative used to be read as "never expires" —
// the least safe reading of what is almost certainly a typo, and one that silently switches invite expiry
// off. Absent still means never; a number now has to be a real number of days, or the app won't boot.
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .Validate(o => o.InviteCodeLifetimeDays is null or > 0,
        "Auth:InviteCodeLifetimeDays must be at least 1, or absent for codes that never expire. " +
        "0 or negative would silently mean 'never', which is not what anyone types 0 to get.")
    .ValidateOnStart();
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
// The chain every AI service sees: MeteredChatClient (daily per-household quotas, managed mode only)
// over ByokChatClient (builds the real provider client from the circuit's settings at call time).
builder.Services.AddScoped<ByokChatClient>();
builder.Services.AddScoped<AiUsageMeter>();
builder.Services.AddScoped<IChatClient, MeteredChatClient>();

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
// Where a household may point its receipt folder. Unset (the self-host default) allows any local path —
// that's the feature there, and the owner is the only tenant. Set it on any multi-household deployment:
// without it the folder setting is an arbitrary-path read of everything the server process can see.
//
// Validated at startup because the failure mode is silent: a root that doesn't resolve leaves the policy
// with nothing to enforce, and "nothing to enforce" is spelled the same as "not configured" — so a typo in
// the security setting would turn confinement off and look exactly like a deployment that never wanted it.
// Refusing to boot is the only way that mistake gets noticed.
builder.Services.AddOptions<ReceiptFolderOptions>()
    .Bind(builder.Configuration.GetSection(ReceiptFolderOptions.SectionName))
    .Validate(o => ReceiptFolderPolicy.RootIsUsable(o.AllowedRoot),
        "Receipts:AllowedRoot is set but isn't a usable path, so receipt folders would silently stop being " +
        "confined. Fix the path, or remove the setting if this deployment doesn't want confinement.")
    .ValidateOnStart();
builder.Services.AddSingleton<ReceiptFolderPolicy>();               // config-only, no per-household state
builder.Services.AddScoped<IReceiptInbox, LocalFolderReceiptInbox>(); // reads the household's folder setting
builder.Services.AddScoped<IReceiptImporter, ReceiptImporter>(); // depends on the scoped IReceiptExtractor
builder.Services.AddScoped<ReceiptConfirmationService>();
builder.Services.AddScoped<ReceiptSelfEval>(); // grades verified receipts on the circuit's key

// Owns where receipt images live on disk (per household), so "delete my data" can reach them and no
// call site does its own path math. Scoped: it files by the current household.
builder.Services.AddScoped<ReceiptStorage>();

builder.Services.AddScoped<ProductRenameService>(); // rename + re-point the name-keyed recipe links
builder.Services.AddScoped<DemoDataSeeder>(); // synthetic demo catalog (guarded: this household's pantry is empty)
// Export + delete-my-data (one place for both). Takes the speech cache root so a delete reaches the
// synthesized audio of the household's recipes, not just its rows.
builder.Services.AddScoped(sp => new UserDataService(
    sp.GetRequiredService<IHouseholdDbFactory>(),
    sp.GetRequiredService<ICurrentHousehold>(),
    sp.GetRequiredService<ReceiptStorage>(),
    sp.GetService<ISpeechCache>(), // null when Speech:CacheMegabytes = 0: no cache, nothing to find or forget
    sp.GetRequiredService<ILogger<UserDataService>>()));

// Voice I/O (ElevenLabs): Scribe = STT (ear), TTS = mouth. Speech is its own REST API, not an
// IChatClient workload, so each rides a typed HttpClient with the base address + xi-api-key header.
// Typed clients are transient (the factory owns handler lifetime) — fine, the services are stateless.
// TTS rides through a disk cache (see SpeechRegistration). Recipe steps are static text, so a recipe
// should cost one synthesis ever — re-reading it shouldn't re-buy audio we already own, or make the
// reader wait on the network to say a sentence it said yesterday.
builder.Services.AddSpeech(builder.Configuration, speechCacheDir);

// Per-circuit ElevenLabs credentials: the visitor's own key from their browser (dev falls back to config).
// Scoped, so concurrent visitors never share a voice key; the speech services read it per request.
builder.Services.AddScoped<CircuitVoiceCredentials>();
builder.Services.AddScoped<IVoiceCredentials>(sp => sp.GetRequiredService<CircuitVoiceCredentials>());

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

// Keep the speech cache from creeping forever. It only grows when text changes (an edited step orphans
// its clip, and its neighbours'), so once at startup is the right cadence — a per-write sweep would put
// a directory scan on the path the cache exists to make fast. The budget is PER HOUSEHOLD (so a heavy
// user can't evict a light one's clips and make them re-buy the audio), which means total disk is
// households × Speech:CacheMegabytes rather than a single ceiling.
if (speechCacheDir is not null)
{
    CachingTextToSpeech.Trim(speechCacheDir, speechCacheMb * 1024L * 1024L,
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SpeechCache"));
}

using (var scope = app.Services.CreateScope())
{
    // Accounts + households (auth.db) — always a from-scratch EnsureCreated (the file is new per
    // deployment site; v3 shipped with no upgrade path for pre-auth pantry DBs — see CLAUDE.md).
    var authFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuthDbContext>>();
    using (var authDb = authFactory.CreateDbContext())
    {
        authDb.Database.EnsureCreated();
        // EnsureCreated never alters an existing file, and auth.db stopped being "fresh per deployment"
        // as soon as a deployment had real accounts in it worth keeping.
        AdditiveSchema.Apply(authDb);
    }

    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ShelfAwareDbContext>>();
    using var db = factory.CreateDbContext();
    // v3's breaking schema change: no in-place upgrade for pre-household DBs — fail fast with
    // instructions instead of a confusing "no such column" on the first query.
    PantryDbGuard.ThrowIfPreHouseholdDb(db);
    db.Database.EnsureCreated();
    // Columns added after v3 shipped (EnsureCreated never alters an existing DB).
    AdditiveSchema.Apply(db);
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
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ReceiptAutoScan");

    // Since v3 the receipt folder is a per-HOUSEHOLD setting, so the scan runs once per household that
    // configured one. Enumerating them needs the raw (unscoped) factory + IgnoreQueryFilters — the one
    // legitimate cross-tenant read here, and it only reads which households to serve, not their data.
    List<string> households;
    try
    {
        using var scope = app.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ShelfAwareDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        households = await db.AppSettings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.ReceiptFolder && s.Value != "" && s.HouseholdId != "")
            .Select(s => s.HouseholdId)
            .Distinct()
            .ToListAsync();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Startup receipt auto-scan failed while listing households.");
        return;
    }

    foreach (var householdId in households)
    {
        try
        {
            // Each household gets its own scope, pinned via UseFixed so the whole import pipeline
            // (settings → inbox → extractor → confirmation) reads and writes only that pantry. With no
            // browser attached, CircuitAiSettings keeps its server-config fallback — the owner key.
            using var scope = app.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ICurrentHousehold>().UseFixed(householdId);
            await scope.ServiceProvider.GetRequiredService<IReceiptImporter>().ImportNewAsync();
        }
        catch (Exception ex)
        {
            // Per household, so one bad folder or receipt doesn't sink the other households' scans.
            logger.LogError(ex, "Startup receipt auto-scan failed for household {HouseholdId}.", householdId);
        }
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

// Signed in, but in no household — send them somewhere that says so.
//
// This has to be middleware rather than a component. Every page resolves its household through
// GetRequiredIdAsync, which THROWS rather than guess a tenant, and the page body initialises before
// anything in the layout gets a chance to intervene — so a component-level guard loses the race and the
// user meets a 500 instead of an explanation. Middleware runs before any of it renders.
//
// Only reachable since members can be removed; before that, every account had a household for life.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var householdless =
        context.User.Identity?.IsAuthenticated == true
        && context.User.FindFirst(HouseholdClaimsPrincipalFactory.HouseholdClaim) is null;

    if (householdless)
    {
        // An API caller can't act on a redirect to an HTML chooser, and the endpoints below would
        // otherwise throw their way to a 500. 403 is the honest answer: authenticated, but nothing here
        // belongs to you.
        if (path.StartsWithSegments("/api"))
        {
            await Results.Problem(
                "Your account isn't in a household, so there's no pantry to act on. Join one at /Account/Household.",
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            return;
        }

        var isAppPage =
            !path.StartsWithSegments("/Account")     // the chooser itself, and sign-in/out
            && !path.StartsWithSegments("/_blazor")  // never redirect the circuit's own transport
            && !path.StartsWithSegments("/_framework")
            && !path.StartsWithSegments("/_content")
            && !path.StartsWithSegments("/demo")     // public, anonymous
            && !Path.HasExtension(path.Value);       // static assets

        if (isAppPage)
        {
            context.Response.Redirect("/Account/Household");
            return;
        }
    }

    await next();
});

app.MapStaticAssets();

// ---- The two /api endpoints, and what "/api" means here ----
//
// This is not an API. It's the two things the browser needs a REAL HTTP request for, which a Blazor
// circuit can't give it: a file download needs an actual response with Content-Disposition (you can't
// push one over the SignalR connection), and the cook-along's ElevenLabs SDK is browser JavaScript that
// has to fetch() its own signed URL. There is no REST surface over the pantry, no tokens, no versioning,
// and nothing here is a contract anyone may depend on. Both are cookie-authenticated and same-origin.
//
// The prefix still earns its keep: three places key off StartsWithSegments("/api") to return a STATUS
// CODE rather than redirect to an HTML page (401 instead of Login, 403 instead of AccessDenied, and 403
// rather than the no-household chooser). Those are exactly the semantics a real API would want too, which
// is why these live here rather than under some /internal/ prefix that would need the same three rules
// duplicated the day a real API shows up.
//
// **If you are adding a real API: put it under /api/v1/ and give it its own auth story.** Versioned means
// "a contract I won't break"; unversioned means "app plumbing, it can move". Two things to decide rather
// than inherit:
//   - These two are not a pair. /api/data/export is genuinely API-shaped and could graduate to
//     /api/v1/export someday. /api/cookalong/signed-url is browser plumbing forever — no API consumer
//     wants "mint a session URL for the SDK running in this page".
//   - The policies above assume COOKIE auth. The moment bearer tokens exist under this prefix,
//     /api/data/export becomes reachable by token too. That may be what you want — but decide it, don't
//     let it happen as a side effect of sharing a path segment.
//
// Renaming these is as cheap now as it will ever be (a handful of string literals, no external
// consumers), so there's nothing to buy by moving them pre-emptively. Decided 2026-07-15.

// Mints a short-lived ElevenLabs signed URL for the cook-along realtime agent, using the VISITOR's own
// key + agent id (sent from their browser over HTTPS) — the app ships with no voice key of its own. The
// key is used only for this call and is never stored or logged; dev/self-host falls back to server config.
// Rate-limited per IP so nobody can spam a visitor's key through it. A custom header is also a mild CSRF
// guard (cross-site forms can't set one).
app.MapGet("/api/cookalong/signed-url", async (HttpContext ctx, IHttpClientFactory httpFactory, IOptions<ElevenLabsOptions> opts, IOptions<LlmOptions> deployment, AiUsageMeter meter, CancellationToken ct) =>
{
    // Managed deployment: the host's voice key is authoritative — ignore any header a browser sends.
    var managed = deployment.Value.IsManaged;

    // Each mint opens a realtime session on the HOST's ElevenLabs key, so managed deployments get a
    // per-household daily quota on top of the per-IP rate limit (unlimited unless configured).
    if (managed && !await meter.MayMintVoiceSessionAsync(ct))
        return Results.Problem("Today's cook-along allowance on this server is used up — it resets tomorrow.",
            statusCode: StatusCodes.Status429TooManyRequests);

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

    if (managed) await meter.RecordVoiceSessionMintAsync(ct);

    // ElevenLabs returns { "signed_url": "wss://..." }; pass it straight through to the client.
    return Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json");
}).RequireRateLimiting("cookalong").RequireAuthorization();

// Full data export ("Download my data") — everything in the household's database as data.json, plus the
// saved receipt images and the audio of any recipe that's been read aloud. Also the "export first"
// offered before Delete my data. Written straight to the response rather than buffered: the receipt
// photos alone can run to tens of megabytes.
app.MapGet("/api/data/export", async (UserDataService data, HttpContext ctx, CancellationToken ct) =>
{
    // ZipArchive is a synchronous API — it writes its data descriptors and central directory with
    // Stream.Write, which Kestrel refuses on a response by default. The only two ways out are to allow it
    // here, or to build the whole archive in memory first and write that asynchronously. Allowing it wins:
    // this endpoint is rare and one-user-at-a-time, so the cost is one thread blocked on writes that
    // mostly land in Kestrel's buffer — whereas buffering would hold every receipt photo in RAM at once,
    // and memory is the scarcer thing on a small deployment.
    ctx.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;

    ctx.Response.ContentType = "application/zip";
    ctx.Response.Headers.ContentDisposition =
        $"attachment; filename=\"shelfaware-data-{DateTime.Now:yyyy-MM-dd}.zip\"";
    await data.WriteArchiveAsync(ctx.Response.Body, ct);
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
