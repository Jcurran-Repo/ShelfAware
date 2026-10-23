using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShelfAware.Core.MealPlanning;
using ShelfAware.Core.Recipes;
using ShelfAware.Core.Settings;
using ShelfAware.Llm;
using ShelfAware.Web.Data;
using ShelfAware.Web.Services;

namespace ShelfAware.Web.Tests;

/// <summary>
/// The real detached runner, in a real DI container: the job runs in a scope of its OWN, so whatever the
/// asking circuit knew has to be carried into it. The household always was; the visitor's AI settings were
/// not — on a BYOK box (no server key) every plan failed "add a key in Settings" a moment after the page's
/// own pre-check, which reads the circuit, had said the key was there.
/// </summary>
public class MealPlanJobsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_BYOK_plan_runs_on_the_visitors_key_and_model_not_the_servers()
    {
        var circuit = Settings();
        circuit.Apply(AiProvider.Anthropic, "visitor-key", "visitor-extract", "visitor-chat");

        var seen = await RunJobAsync(circuit.Carry());

        Assert.Equal("visitor-key", seen.ApiKey);
        Assert.Equal("visitor-chat", seen.ChatModel);
        Assert.Equal("visitor-extract", seen.ExtractionModel);
    }

    [Fact]
    public async Task With_nothing_from_the_browser_the_job_keeps_the_server_config()
    {
        // Carry() is null when the circuit is on server config, and the job's fresh scope then builds the same
        // server config itself — the managed and self-hosted cases, where there is nothing to carry.
        var circuit = Settings();

        Assert.Null(circuit.Carry());
        var seen = await RunJobAsync(circuit.Carry());

        Assert.Equal("", seen.ApiKey);
        Assert.Equal("server-chat", seen.ChatModel);
    }

    [Fact]
    public void A_carried_copy_is_held_to_the_same_guards_as_the_browser()
    {
        // The SSRF guard lives in Apply, and a carried copy goes back through it: a scope on a box that does
        // not allow custom endpoints can never be pointed at one by a value captured somewhere else.
        var permissive = new CircuitAiSettings(Options.Create(new LlmOptions { KeyMode = "Byok", AllowCustomEndpoint = true }));
        permissive.Apply(AiProvider.OpenAICompatible, "k", null, null, "http://internal-host/v1");
        var strict = Settings();

        strict.Apply(permissive.Carry()!);

        Assert.Null(strict.BaseUrl);
    }

    [Fact]
    public void A_carried_copy_does_not_print_the_key()
    {
        var circuit = Settings();
        circuit.Apply(AiProvider.Anthropic, "sk-visitor-secret", null, null);

        Assert.DoesNotContain("sk-visitor-secret", circuit.Carry()!.ToString(), StringComparison.Ordinal);
    }

    // A BYOK box: the server has no key of its own, so anything the job's scope knows about a key came from
    // the visitor.
    private static IOptions<LlmOptions> Server() =>
        Options.Create(new LlmOptions { KeyMode = "Byok", ApiKey = "", ChatModel = "server-chat", ExtractionModel = "server-extract" });

    private static CircuitAiSettings Settings() => new(Server());

    private async Task<SeenSettings> RunJobAsync(CircuitAiSettings.Carried? carried)
    {
        var seen = new SeenSettings();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Server());
        services.AddSingleton<IHouseholdDbFactory>(_db);
        services.AddSingleton<IAppSettings>(new FakeAppSettings());
        services.AddScoped<ICurrentHousehold>(_ => new FakeCurrentHousehold(null));
        services.AddScoped<CircuitAiSettings>();
        services.AddScoped<IMealPlanGenerator>(sp => new RecordingGenerator(sp.GetRequiredService<CircuitAiSettings>(), seen));
        services.AddScoped<MealPlanService>();
        await using var provider = services.BuildServiceProvider();
        var jobs = new MealPlanJobs(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<MealPlanJobs>.Instance);

        jobs.Start("household-under-test", carried);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (jobs.Current("household-under-test") is { State: MealPlanJobState.Running })
        {
            Assert.True(DateTime.UtcNow < deadline, "the background job never finished");
            await Task.Delay(10);
        }
        Assert.Equal(MealPlanJobState.Done, jobs.Current("household-under-test")!.State);
        return seen;
    }

    private sealed class SeenSettings
    {
        public string? ApiKey { get; set; }
        public string? ChatModel { get; set; }
        public string? ExtractionModel { get; set; }
    }

    /// <summary>Stands in for the AI generator and reports what ITS scope's settings said — which is what the
    /// real generator's client would have been built from.</summary>
    private sealed class RecordingGenerator(CircuitAiSettings settings, SeenSettings seen) : IMealPlanGenerator
    {
        public Task<IReadOnlyList<RecipeSuggestion>> GenerateAsync(MealPlanBatch batch, CancellationToken cancellationToken = default)
        {
            seen.ApiKey = settings.ApiKey;
            seen.ChatModel = settings.ChatModel;
            seen.ExtractionModel = settings.ExtractionModel;
            return Task.FromResult<IReadOnlyList<RecipeSuggestion>>(
                [new RecipeSuggestion("Tacos", "A Tacos dinner.", [new SuggestedIngredient("beans", true, null, "1 can")], ["Cook it."], 500)]);
        }
    }
}
