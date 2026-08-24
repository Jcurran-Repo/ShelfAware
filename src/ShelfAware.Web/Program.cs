using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using HotChocolate.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using System.Text.Json;
using ShelfAware.Core.Census;
using ShelfAware.Core.Chat;
using ShelfAware.Core.Domain;
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
using ShelfAware.Web.Diagnostics;
using ShelfAware.Web.GraphQL;
using ShelfAware.Web.Ingest;
using ShelfAware.Web.Services;
using ShelfAware.Web.Undo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // Voice sends a recorded utterance from the browser to .NET as base64 over the circuit; the 32 KB
    // default is too small for even a few seconds of audio. 4 MB comfortably covers a push-to-talk bark.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024);

// SQLite lives in a configurable data directory: ./app-data locally; a cloud box sets DataDir
// (the droplet runbook uses /var/lib/shelfaware — see docs/deploy-droplet.md).
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

// Read-only GraphQL API exposure is a pure config flag (default false), NOT an env.IsDevelopment()
// lock — the API is meant to reach prod, so enabling it there is a config flip, not a code change.
// The ApiToken scheme, its policy, the endpoint, and the Settings UI all gate on this one flag.
var graphQlEnabled = builder.Configuration.GetValue<bool>("GraphQL:Enabled");

var authentication = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
});
authentication.AddIdentityCookies();

// The API-token scheme rides alongside the cookie schemes but is NEVER the default — it runs only when
// the GraphQL endpoint's policy names it. Registered only when the API is enabled, so a deployment with
// the flag off has no extra auth surface at all.
if (graphQlEnabled)
{
    authentication.AddScheme<ApiTokenAuthenticationOptions, ApiTokenAuthenticationHandler>(
        ApiTokenAuthenticationHandler.SchemeName, configureOptions: null);
}

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
    // HttpOnly + SameSite=Lax are the defaults; Secure is enforced in production (the tailnet/droplet
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
    // Sign-in never requires a confirmed address: the only mail the app can send — and only when
    // Email: is configured at all — is the password reset (EmailOptions; config-gated).
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

// The Admin policy guards /admin (routed pages go through AuthorizeRouteView, which enforces it).
// The policy binds its own snapshot of the Admin: section at startup; pages read the same section
// via IOptions<AdminOptions>. One SECTION and one PREDICATE (AdminOptions.IsAdmin) — a config
// change needs the restart every other option here already needs. Unset = the policy refuses
// everyone, so an unconfigured deployment has zero admin surface (the Google-OAuth posture).
var adminOptions = builder.Configuration.GetSection(AdminOptions.SectionName).Get<AdminOptions>() ?? new();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AdminOptions.PolicyName,
        policy => policy.RequireAssertion(ctx => adminOptions.IsAdmin(ctx.User)));

    // The GraphQL endpoint requires the ApiToken scheme SPECIFICALLY (so a browser cookie can't reach
    // it) plus an authenticated token. RequireAuthenticatedUser + AddAuthenticationSchemes together are
    // what make PolicyEvaluator run the scheme and promote its household-claim principal to
    // HttpContext.User — the tenancy hand-off. Registered only when the API is enabled.
    if (graphQlEnabled)
    {
        options.AddPolicy(ApiTokenAuthenticationHandler.PolicyName, policy => policy
            .AddAuthenticationSchemes(ApiTokenAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser());
    }
});
builder.Services.AddOptions<AdminOptions>()
    .Bind(builder.Configuration.GetSection(AdminOptions.SectionName));
// Validated at STARTUP, not trusted. A lifetime of 0 or negative used to be read as "never expires" —
// the least safe reading of what is almost certainly a typo, and one that silently switches invite expiry
// off. Absent still means never; a number now has to be a real number of days, or the app won't boot.
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .Validate(o => o.InviteCodeLifetimeDays is null or > 0,
        "Auth:InviteCodeLifetimeDays must be at least 1, or absent for codes that never expire. " +
        "0 or negative would silently mean 'never', which is not what anyone types 0 to get.")
    .ValidateOnStart();
// Password-reset email (the app's only outbound mail). All-or-nothing, validated at startup: a
// wholly absent Email: section means the feature is off everywhere it shows (the sign-in link,
// /Account/ForgotPassword, Settings' wording — all gated on the ONE EmailOptions.IsConfigured);
// a partially present one is almost certainly a typo'd deploy, so the app won't boot rather than
// half-work.
builder.Services.AddOptions<EmailOptions>()
    .Bind(builder.Configuration.GetSection(EmailOptions.SectionName))
    .Validate(o => o.IsConfigured || o.IsWhollyAbsent,
        "Email: is partially configured. Set BOTH Email:SmtpHost and Email:From (plus " +
        "Email:SmtpUser/Email:SmtpPassword if your relay needs them), or remove the whole section.")
    .Validate(o => o.CredentialsPaired,
        "Email:SmtpUser and Email:SmtpPassword go together — set both or neither.")
    .Validate(o => o.SmtpPort > 0, "Email:SmtpPort must be a positive port number.")
    .ValidateOnStart();
builder.Services.AddSingleton<IAccountMailer, SmtpAccountMailer>();
builder.Services.AddScoped<HouseholdService>();
// Mint/validate/list/revoke for read-only GraphQL API tokens (credentials in auth.db). Registered
// always — the auth handler and Settings gate their EXPOSURE on GraphQL:Enabled, but the service
// itself is harmless dormant and the delete-my-data flow may need it regardless.
builder.Services.AddScoped<ApiTokenService>();

// ---- Read-only GraphQL API (gated on GraphQL:Enabled) ----
// The schema is registered only when the API is enabled, so a disabled deployment builds no schema and
// maps no endpoint. Every resolver reads through IHouseholdDbFactory (Query.cs), so the household query
// filter scopes each read for free — no new IgnoreQueryFilters, no Mutation type (read-only by absence).
if (graphQlEnabled)
{
    builder.Services.AddPantryGraphQL(includeExceptionDetails: builder.Environment.IsDevelopment());
}

// ---- In-app problem reporting ----
// The error log: a logging provider captures every Error/Critical event (handled ones included —
// the house catch-log-and-say-so convention is exactly what feeds it) into a bounded channel; a
// background writer persists them deduped-by-fingerprint into auth.db, trimmed at
// ErrorLogStore.MaxRows. Always on: it is bounded, admin-only to read, and the family box
// otherwise logs to a console nobody watches. AdminReportReader is the one page-facing surface
// for BOTH halves (errors + cross-household bug reports) and carries the admin gate.
var errorSink = new ErrorLogSink();
builder.Logging.AddProvider(new ErrorLogCaptureProvider(errorSink));
builder.Services.AddSingleton(errorSink);
builder.Services.AddSingleton<ErrorLogStore>();
builder.Services.AddHostedService<ErrorLogWriter>();
builder.Services.AddScoped<AdminReportReader>();
// Its write sibling: resolve/reopen, the app's one cross-household write — see the class doc.
builder.Services.AddScoped<ReportResolutionService>();
builder.Services.AddScoped<AdminHouseholdService>(); // the /admin household roster + the Founder grant

// ---- Who's using the app: the admin "logins + who's online" view ----
// LoginAudit persists per-account login counts (auth.db operator data, like the error log; read through
// AdminReportReader's gate). OnlinePresence is the live half — a singleton fed by a per-circuit
// CircuitHandler as connections come and go. The recorder is called from the sign-in sites only.
builder.Services.AddSingleton<LoginAudit>();
builder.Services.AddSingleton<OnlinePresence>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, PresenceCircuitHandler>();

// Auth cookies + antiforgery tokens are encrypted with DataProtection keys. Persist them next to the
// DBs (app-data is gitignored and survives republish) — otherwise every restart/redeploy would sign
// the whole household out and invalidate in-flight forms.
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("ShelfAware");
if (OperatingSystem.IsWindows())
{
    // Encrypt the key ring at rest with MACHINE-scope DPAPI, not user-scope. User-scope needs the
    // user's credential material, which a boot-time task (S4U, no stored password) doesn't reliably
    // have before anyone logs on — and worse, an S4U logon re-encrypting the user's master keys is
    // exactly what corrupted them on 2026-07-17 and 500'd every auth page after the next reboot.
    // Machine scope decrypts in any logon session. Trade-off: any local process can read the key
    // ring — the same trust boundary as the SQLite DBs sitting next to it. On Linux (the droplet)
    // the keys stay plain files under the data dir, so this was never a stronger guarantee than that.
    dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
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
builder.Services.AddScoped<IEntitlements, Entitlements>(); // the current household's tier — the meter's Founder exemption + the Settings badge
builder.Services.AddScoped<AiUsageMeter>();
builder.Services.AddScoped<IChatClient, MeteredChatClient>();

// Per-circuit bus wiring the layout voice agent to the pages (data-changed refresh + resume hand-off).
builder.Services.AddScoped<VoiceCoordinator>();
builder.Services.AddScoped<TourCoordinator>(); // lets a page start the layout-hosted guided walkthrough

// The AI services depend (directly or transitively) on the per-circuit IChatClient, so they're scoped —
// a singleton can't hold a scoped dependency. Since v3 the data services are scoped too: they read
// through IHouseholdDbFactory, which needs the scope's signed-in user to know whose pantry it is.
builder.Services.AddScoped<IReceiptExtractor, AnthropicReceiptExtractor>();
builder.Services.AddScoped<IShelfCensusReader, AnthropicShelfCensusReader>(); // §13.8: proposes what's on a shelf
builder.Services.AddScoped<IRecipeImporter, AnthropicRecipeImporter>(); // photo/text → a reviewable recipe
// The browser half of the same flow: downscales a picked photo before it crosses the circuit. Stateless,
// so singleton; separate from the reader because it's a browser seam rather than an AI one.
builder.Services.AddSingleton<IShelfPhotoLoader, BrowserShelfPhotoLoader>();
builder.Services.AddScoped<IPantryStore, EfPantryStore>();
builder.Services.AddScoped<IPantryChat, AnthropicPantryChat>();
builder.Services.AddScoped<ITagAdvisor, AnthropicTagAdvisor>();
builder.Services.AddScoped<IRecipeTagAdvisor, AnthropicRecipeTagAdvisor>();
builder.Services.AddScoped<IRecipeAdvisor, AnthropicRecipeAdvisor>();
builder.Services.AddScoped<IProductSubstituteAdvisor, AnthropicProductSubstituteAdvisor>();
builder.Services.AddScoped<IIngredientAlternativesAdvisor, AnthropicIngredientAlternativesAdvisor>();
builder.Services.AddScoped<IRecipeAdapter, RecipeAdapter>();
builder.Services.AddScoped<RecipeTagService>(); // the one recipe-tag write path (cookbook + import)

// Receipts arrive by upload only (the folder inbox was retired 2026-07-22 — an arbitrary-path read the
// box shouldn't carry once it's shared, and uploads had superseded it). The settings store backs the
// /settings page; both the upload Smart/Auto router and the manual review confirm receipts through the
// ONE shared confirmation service.
builder.Services.AddScoped<IAppSettings, EfAppSettings>();          // settings are per household now
builder.Services.AddScoped<ReceiptAutoConfirmer>(); // routes an uploaded receipt per the household's ImportMode
builder.Services.AddScoped<ReceiptDuplicateDetector>(); // "is this a re-upload?" — a detected dupe never auto-confirms
builder.Services.AddScoped<ReceiptConfirmationService>();
builder.Services.AddScoped<ReceiptIngestionService>(); // images → PendingReview receipt + auto-confirm route (the page and the upload endpoint share it)
builder.Services.AddScoped<ReceiptRemovalService>(); // the confirm's inverse — the duplicate-upload escape hatch
// The census's OWN confirm path, deliberately not the receipt one: a shelf photo writes counts and must
// never write a PurchaseEvent (§13.8's ★ rule).
builder.Services.AddScoped<CensusConfirmationService>();
builder.Services.AddScoped<ReceiptSelfEval>(); // grades verified receipts on the circuit's key

// Owns where receipt images live on disk (per household), so "delete my data" can reach them and no
// call site does its own path math. Scoped: it files by the current household.
builder.Services.AddScoped<ReceiptStorage>();
// The narrow seam the receipt-confirm undo deletes an image through (same instance as ReceiptStorage).
builder.Services.AddScoped<IReceiptImageCleanup>(sp => sp.GetRequiredService<ReceiptStorage>());
builder.Services.AddScoped<RecipeImageStorage>(); // where recipe photos live (per household); mirrors ReceiptStorage
// The narrow seam the recipe-save/adapt undo reaps a photo through (same instance as RecipeImageStorage).
builder.Services.AddScoped<IRecipeImageCleanup>(sp => sp.GetRequiredService<RecipeImageStorage>());

builder.Services.AddScoped<ProductRenameService>(); // rename + re-point the name-keyed recipe links
builder.Services.AddScoped<ProductMergeService>();  // fold a variety-split product into its item

// The activity log + per-action undo (and the /history page). EfPantryStore and the confirm/edit
// services record through IActivityLog in the data layer, so chat/voice actions are logged for free;
// both the inline "↩ Undo" and /history reverse through the one ActivityLogService.UndoAsync.
builder.Services.AddActivityLog(builder.Configuration);
builder.Services.AddScoped<ReportDataService>();    // joins EF rows into the report engine's flat facts
builder.Services.AddScoped<DemoDataSeeder>(); // synthetic demo catalog (guarded: this household's pantry is empty)
// Export + delete-my-data (one place for both). Takes the speech cache root so a delete reaches the
// synthesized audio of the household's recipes, not just its rows.
builder.Services.AddScoped(sp => new UserDataService(
    sp.GetRequiredService<IHouseholdDbFactory>(),
    sp.GetRequiredService<ICurrentHousehold>(),
    sp.GetRequiredService<ReceiptStorage>(),
    sp.GetRequiredService<RecipeImageStorage>(),
    sp.GetService<ISpeechCache>(), // null when Speech:CacheMegabytes = 0: no cache, nothing to find or forget
    sp.GetRequiredService<ApiTokenService>(), // token metadata for export + removal on delete-my-data (auth.db)
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
    // The GraphQL API's 429 needs a body, for the same reason its 401 does: an empty error response is
    // re-executed by UseStatusCodePages (method preserved) into POST /not-found → antiforgery → a
    // misleading 400. Writing a small JSON body starts the response so the real 429 stands (and it's the
    // shape a GraphQL client expects). Scoped to /graphql so the other rate-limit policies are unchanged.
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.HttpContext.Request.Path.StartsWithSegments("/graphql"))
        {
            context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
            await context.HttpContext.Response.WriteAsync(
                """{"errors":[{"message":"Too many requests — slow down and retry shortly."}]}""", ct);
        }
    };
    o.AddPolicy("cookalong", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Receipt/shelf-photo uploads: each is an AI (vision) call, metered per household in managed mode, so
    // this is just an anti-hammer brake per IP. Generous enough for a legitimate batch (one request each).
    o.AddPolicy("photo-upload", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // The read-only GraphQL API throttles per TOKEN, not per IP — prod sits behind Caddy/Cloudflare/
    // Tailscale, so per-IP would bucket every caller under one proxy address. The token id rides in a
    // claim the ApiToken scheme sets, and a request only reaches the limiter after passing authorization
    // (an unauthenticated one is 401'd earlier), so the claim is present. 120/min is generous for a real
    // client and caps a runaway.
    o.AddPolicy("graphql", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.FindFirst(ApiTokenAuthenticationHandler.TokenIdClaim)?.Value ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
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
        // STRICTLY AFTER the additive pass, which is what puts the three Invite columns on a pre-7/15
        // auth.db — the rebuild copies them by name, so it needs them to exist first. One-off; see the
        // class docs for why it's the exception to AdditiveSchema's additive-only rule.
        NullableInviteCodeMigration.Apply(authDb);
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

// Behind a TLS-terminating reverse proxy (Tailscale Serve for the private self-host, Caddy on the
// droplet — see deploy/Caddyfile), honor
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
        // blob: is required by Blazor's InputFile image resize (RequestImageFileAsync loads the picked
        // photo into an <img> via a blob: URL to draw it on a canvas). Without it the load is blocked
        // and Blazor's JS never settles the interop promise, so photo uploads hang forever. blob: URLs
        // are same-origin objects mintable only by our own scripts, which script-src already locks down.
        "img-src 'self' data: blob:; " +
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
// Antiforgery guards the Blazor cookie/form world; the GraphQL endpoint is deliberately outside it. It
// authenticates by BEARER TOKEN, not a cookie, so there is no ambient credential a cross-site form could
// ride (CSRF doesn't apply), and it's read-only besides — nothing to forge. Its endpoint declares form
// acceptance (Hot Chocolate's multipart upload support), so without this skip the antiforgery middleware
// 400s every token request before the resolver runs. Only /graphql is exempted; the rest of the app keeps
// full antiforgery. (The tidy per-endpoint .DisableAntiforgery() can't be used — it routes through
// IEndpointConventionBuilder.Finally(), which Hot Chocolate's endpoint builder does not implement.)
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/graphql"),
    branch => branch.UseAntiforgery());
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

// A recipe's photo, served household-scoped. The recipe is looked up through the caller's household
// filter (IHouseholdDbFactory pre-sets the household from the auth claim), so an id belonging to another
// household simply isn't found — a 404, never a cross-household read. The stored path is read from that
// filtered row, NEVER from the request, and RecipeImageStorage's own guard keeps it inside the store.
// Under /api so the no-household middleware answers 401/403 rather than an HTML redirect.
app.MapGet("/api/recipe-image/{id:int}", async (
    int id, IHouseholdDbFactory dbFactory, RecipeImageStorage images, HttpContext ctx, CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var path = await db.Recipes.AsNoTracking().Where(r => r.Id == id).Select(r => r.ImagePath).FirstOrDefaultAsync(ct);
    if (string.IsNullOrEmpty(path)) return Results.NotFound();
    var image = await images.ReadAsync(path, ct);
    if (image is null) return Results.NotFound();
    // Private (a per-household photo — never a shared cache) and short-lived; the ?v the cookbook appends
    // changes on every replace (a fresh GUID filename), so a new photo is fetched fresh regardless.
    ctx.Response.Headers.CacheControl = "private, max-age=3600";
    return Results.File(image.Value.Bytes, image.Value.MediaType);
}).RequireAuthorization();

// A receipt's saved copy, served household-scoped as a download — the same tenancy shape as
// /api/recipe-image above: the receipt is looked up through the caller's household filter, so a foreign id
// is a 404 (never a cross-household read or an existence oracle), the folder path is read from that
// filtered row (NEVER the request), and ReceiptStorage's Within guard keeps the reads inside the store.
// One saved page downloads as its own image/PDF; several download as a zip. Under /api so the no-household
// middleware answers 401/403 rather than an HTML redirect.
app.MapGet("/api/receipt-image/{id:int}", async (
    int id, IHouseholdDbFactory dbFactory, ReceiptStorage storage, HttpContext ctx, CancellationToken ct) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var row = await db.Receipts.AsNoTracking().Where(r => r.Id == id)
        .Select(r => new { r.ImagePath, r.Merchant, r.PurchasedAt }).FirstOrDefaultAsync(ct);
    if (row is null || string.IsNullOrEmpty(row.ImagePath)) return Results.NotFound();

    var baseName = ReceiptFileName.ForDownload(row.Merchant, row.PurchasedAt, id);
    var download = await storage.ReadForDownloadAsync(row.ImagePath, baseName, ct);
    if (download is null) return Results.NotFound();

    ctx.Response.Headers.CacheControl = "private, no-store"; // a photo of a personal receipt — don't cache
    return Results.File(download.Value.Bytes, download.Value.MediaType, download.Value.FileName);
}).RequireAuthorization();

// Receipt upload — the browser resizes each photo and POSTs the bytes HERE, off the SignalR circuit, so a
// mobile file-pick that briefly drops the circuit can't lose the upload (the bug this endpoint exists to
// fix: the InputFile change event fired while the circuit was down and was lost forever). ONE request =
// ONE receipt — a long receipt's photos arrive as several parts in one request; a batch of separate
// receipts is several requests. Cookie-authed + household-scoped like the rest of /api (ReceiptIngestionService
// goes through IHouseholdDbFactory). Antiforgery, size/type/count limits, and the BYOK key are handled by
// PhotoUploadIntake — the shared front door both photo endpoints use.
app.MapPost("/api/receipts/extract", async (
    HttpRequest request, HttpContext ctx, IAntiforgery antiforgery, CircuitAiSettings ai,
    ReceiptIngestionService ingestion, ILoggerFactory logs, CancellationToken ct) =>
{
    var (files, error) = await PhotoUploadIntake.ReadAsync(request, ctx, antiforgery,
        mt => mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || mt == "application/pdf",
        "Only photos and PDFs are accepted.", ct);
    if (error is not null) return error;

    PhotoUploadIntake.ApplyByok(request, ai);
    var pages = files!.Select(f => new ReceiptAttachment(f.Bytes, f.MediaType)).ToList();
    try
    {
        var outcome = await ingestion.IngestAsync(pages, ct);
        return Results.Ok(outcome);
    }
    catch (OperationCanceledException) { throw; } // the client went away — nothing to report
    catch (Exception ex)
    {
        logs.CreateLogger("ReceiptUpload").LogError(ex, "Receipt upload extraction failed.");
        return Results.Json(new { error = "Sorry — something went wrong reading that receipt. Please try again." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization().RequireRateLimiting("photo-upload");

// Shelf-census upload — the browser resizes each shelf photo and POSTs it HERE, off the circuit (same
// reason as the receipt endpoint). The reader PROPOSES what's on the shelf; nothing is persisted — the
// photo of someone's freezer goes to the model and stops there (§13.8), which is why there's no storage or
// audit copy here. ONE request = ONE census over all the photos it carries. Images only (nobody prints a
// freezer to PDF). The raw model output is deliberately NOT shipped back — it's debug-only and can be large.
app.MapPost("/api/pantry-photo/read", async (
    HttpRequest request, HttpContext ctx, IAntiforgery antiforgery, CircuitAiSettings ai,
    IShelfCensusReader reader, IHouseholdDbFactory dbFactory, ILoggerFactory logs, CancellationToken ct) =>
{
    // maxFiles: 8 matches the census page's own cap (PantryPhoto.MaxPhotos — the shelf reader looks at a
    // handful per go), enforced here server-side, not just in the UI.
    var (files, error) = await PhotoUploadIntake.ReadAsync(request, ctx, antiforgery,
        mt => mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
        "Only photos are accepted.", ct, maxFiles: 8);
    if (error is not null) return error;

    PhotoUploadIntake.ApplyByok(request, ai);
    var photos = files!.Select(f => new ShelfPhoto(f.Bytes, f.MediaType)).ToList();
    try
    {
        List<string> names;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            names = await db.Products.AsNoTracking().OrderBy(p => p.Name).Select(p => p.Name).Distinct().ToListAsync(ct);
        var result = await reader.ReadAsync(photos, names, ct);
        return Results.Ok(new { result.Success, result.Error, result.Items });
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        logs.CreateLogger("ShelfCensus").LogError(ex, "Shelf census read failed.");
        return Results.Json(new { error = "Sorry — something went wrong reading those photos. Please try again." },
            statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization().RequireRateLimiting("photo-upload");

// PWA manifest — makes the app installable ("Add to home screen"). Served explicitly so the content type
// is right regardless of static-file MIME config; it loads under the same-origin CSP (manifest-src falls
// back to default-src 'self'). No service worker: this is a server-rendered app, so there's no offline mode.
//
// The icon "src"s carry a short content hash as ?v=, so the URL changes whenever the art changes and a
// phone/CDN that cached an older icon under the old path is forced to re-fetch instead of installing the
// stale one. The head links (App.razor) get the same effect from @Assets/MapStaticAssets; the manifest
// can't use @Assets — it's handed to Razor components by the renderer, not via DI, so a minimal-API GET
// param of that type is inferred as a request body and throws at startup — so it hashes the bytes itself.
// Computed ONCE at startup (icons don't change without a redeploy, which restarts the app).
string IconSrc(string file)
{
    // WebRootPath is null when wwwroot can't be located (a misconfigured content root / working directory —
    // the deploy gotcha class). Guard it explicitly: Path.Combine would throw ArgumentNullException, which the
    // IO catch below deliberately doesn't cover, and this helper exists precisely NOT to crash startup.
    var webRoot = app.Environment.WebRootPath;
    if (string.IsNullOrEmpty(webRoot))
    {
        app.Logger.LogWarning("WebRootPath is not set; serving PWA icon {File} unversioned.", file);
        return $"/icons/{file}";
    }
    try
    {
        var bytes = File.ReadAllBytes(Path.Combine(webRoot, "icons", file));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..8].ToLowerInvariant();
        return $"/icons/{file}?v={hash}";
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // A missing/unreadable icon must not take the whole app down over a cache-bust — serve it unversioned.
        app.Logger.LogWarning(ex, "Couldn't hash icon {File} for the PWA manifest; serving it unversioned.", file);
        return $"/icons/{file}";
    }
}
var icon192Src = IconSrc("icon-192.png");
var icon512Src = IconSrc("icon-512.png");

app.MapGet("/manifest.webmanifest", () => Results.Content($$"""
{
  "name": "Shelf Aware",
  "short_name": "ShelfAware",
  "description": "Know what you're running low on before you run out.",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "background_color": "#131619",
  "theme_color": "#131619",
  "icons": [
    { "src": "{{icon192Src}}", "sizes": "192x192", "type": "image/png", "purpose": "any" },
    { "src": "{{icon512Src}}", "sizes": "512x512", "type": "image/png", "purpose": "any" },
    { "src": "{{icon512Src}}", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
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

// GET /dev/login — a Development-only sign-in past the auth wall. Self-gating: maps nothing unless
// DevAuth.IsEnabled (Development + the Dev:QuickLogin flag), so this line is inert on every real
// deployment. See ShelfAware.Web.Auth.DevAuth.
app.MapDevQuickLogin();

// The read-only GraphQL endpoint (gated on GraphQL:Enabled). Requires the ApiToken policy specifically:
// that is what makes PolicyEvaluator run the token scheme and promote its household-claim principal to
// HttpContext.User — so a browser cookie can't reach it and every resolver scopes to the token's
// household. Introspection stays behind the token too (no anonymous schema).
//
// MapGraphQLHttp, NOT MapGraphQL: HTTP transport only. There are no subscriptions, so the WebSocket
// transport MapGraphQL would also map is pure attack surface — a query sent over an established socket
// acquires the per-token rate-limit lease only once at the upgrade, sidestepping the 120/min cap. It
// also drops the in-browser Nitro IDE, which was unreachable anyway (the whole endpoint requires the
// bearer token, so a plain browser navigation to it just 401s) — clients use curl/Postman or the Nitro
// desktop app with a Bearer header, per docs/graphql-api.md.
if (graphQlEnabled)
{
    app.MapGraphQLHttp()
        .RequireAuthorization(ApiTokenAuthenticationHandler.PolicyName)
        .RequireRateLimiting("graphql");
}

app.Run();
