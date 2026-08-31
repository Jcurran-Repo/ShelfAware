using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace ShelfAware.Web.Data;

/// <summary>
/// Runs meal-plan generation as a DETACHED background job, one per household, so a long generation (many
/// AI calls over several minutes) survives the user navigating away or closing the tab — they pick the
/// result up when they come back. The page starts a job and polls <see cref="Current"/> for progress; the
/// work itself runs in its own DI scope on <see cref="CancellationToken.None"/>, unhooked from any circuit.
/// <para>Singleton. In-memory: a job in flight when the process restarts is lost (the user regenerates),
/// but a FINISHED plan is already persisted, so only the rare mid-flight case is affected.</para>
/// </summary>
/// <summary>The page's view of background generation — start a job, and read its current status. Behind an
/// interface so the page can be tested with a fake, while the real detached runner gets its own test.</summary>
public interface IMealPlanJobs
{
    void Start(string householdId);
    MealPlanJobSnapshot? Current(string householdId);
}

public sealed class MealPlanJobs(IServiceScopeFactory scopeFactory, ILogger<MealPlanJobs> logger) : IMealPlanJobs
{
    private readonly ConcurrentDictionary<string, MealPlanJob> _jobs = new();

    /// <summary>The household's current (or most recent) job status, or null if none has run this process
    /// lifetime.</summary>
    public MealPlanJobSnapshot? Current(string householdId) =>
        _jobs.TryGetValue(householdId, out var job) ? job.Snapshot() : null;

    /// <summary>Start a generation for the household unless one is already running, so a double-click or a
    /// second tab doesn't launch two.</summary>
    public void Start(string householdId)
    {
        while (true)
        {
            if (_jobs.TryGetValue(householdId, out var existing))
            {
                if (existing.IsRunning) return;                       // already generating — leave it be
                var replacement = new MealPlanJob();
                if (_jobs.TryUpdate(householdId, replacement, existing)) { Launch(householdId, replacement); return; }
            }
            else
            {
                var fresh = new MealPlanJob();
                if (_jobs.TryAdd(householdId, fresh)) { Launch(householdId, fresh); return; }
            }
            // Lost the race to another thread — loop and re-read what's there now.
        }
    }

    private void Launch(string householdId, MealPlanJob job) => _ = Task.Run(() => RunAsync(householdId, job));

    private async Task RunAsync(string householdId, MealPlanJob job)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            // Pin the household on the fresh scope (no circuit / HttpContext here to infer it from) — the
            // same UseFixed pattern the startup work and DevAuth seeding use.
            scope.ServiceProvider.GetRequiredService<ICurrentHousehold>().UseFixed(householdId);
            var planner = scope.ServiceProvider.GetRequiredService<MealPlanService>();
            // CancellationToken.None on purpose: a background plan must finish even after the circuit that
            // asked for it is gone — that's the whole point of running it detached.
            var result = await planner.GenerateAsync(job.Progress, CancellationToken.None);
            job.Complete(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background meal-plan generation failed for household {Household}.", householdId);
            job.Fail("Couldn't generate a plan just now — please try again.");
        }
    }
}

public enum MealPlanJobState { Running, Done, Failed }

/// <summary>One household's generation job. Written by the background task, read by the circuit (the page's
/// poll), so every access is under a lock and callers read an immutable <see cref="MealPlanJobSnapshot"/>.</summary>
public sealed class MealPlanJob
{
    private readonly object _gate = new();
    private MealPlanJobState _state = MealPlanJobState.Running;
    private int _done, _total, _mealCount;
    private string? _error;

    public bool IsRunning { get { lock (_gate) { return _state == MealPlanJobState.Running; } } }

    public MealPlanJobSnapshot Snapshot()
    {
        lock (_gate) { return new MealPlanJobSnapshot(_state, _done, _total, _mealCount, _error); }
    }

    internal void Progress(int done, int total) { lock (_gate) { _done = done; _total = total; } }

    internal void Complete(MealPlanResult result)
    {
        lock (_gate)
        {
            _state = result.Succeeded ? MealPlanJobState.Done : MealPlanJobState.Failed;
            _mealCount = result.MealCount;
            _error = result.Error;
        }
    }

    internal void Fail(string error) { lock (_gate) { _state = MealPlanJobState.Failed; _error = error; } }
}

public sealed record MealPlanJobSnapshot(MealPlanJobState State, int Done, int Total, int MealCount, string? Error);
