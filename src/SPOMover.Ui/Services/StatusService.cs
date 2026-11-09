using System.Collections.Concurrent;
using SPOMover.Engine;
using SPOMover.State;

namespace SPOMover.Ui.Services;

/// <summary>
/// GUI-1, NFR-2: state of a run for the dashboard and the API. Counters come from the state database
/// (so they always match it) through indexed queries; for dashboards one result is shared by all viewers for a second,
/// so several open dashboards do not multiply the load. The API asks for a fresh one: a client that has just sent a
/// command must see its effect. Live values (speed, RU, throttling) come from the engine.
/// </summary>
public sealed class StatusService(RunService runs)
{
    private readonly ConcurrentDictionary<long, (DateTimeOffset At, RunStatus Status)> _cache = new();

    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(1);

    public RunStatus? Get(long id, bool fresh = false)
    {
        if (!fresh && _cache.TryGetValue(id, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheFor)
            return cached.Status;
        var status = Build(id);
        if (status is not null)
            _cache[id] = (DateTimeOffset.UtcNow, status);
        return status;
    }

    private RunStatus? Build(long id)
    {
        var run = runs.Store.GetRun(id);
        if (run is null)
            return null;
        var (files, bytes) = runs.Store.Totals(id);
        var active = runs.Active;
        bool isActive = active is not null && active.RunId == id && !active.Completion.IsCompleted;
        return new RunStatus(
            RunInfo.From(run),
            runs.Store.CountByStatus(id),
            files,
            bytes,
            runs.Store.CountNonFinal(id),
            runs.Store.CountAttention(id),
            isActive,
            isActive && active!.Engine.IsPaused,
            isActive ? active!.Engine.Metrics.Snapshot() : null,
            runs.Store.CountCheckouts(id),
            runs.Store.GetNotes(id),
            run.Mode == RunModes.Inventory ? runs.Store.GetTargetProgress(id) : null);
    }

    /// <summary>The run the dashboard shows by default: the active one, otherwise the latest.</summary>
    public long? CurrentRunId() => runs.Active is { } a && !a.Completion.IsCompleted ? a.RunId : runs.Store.ListRuns(1).FirstOrDefault()?.Id;

    /// <summary>Estimated time to finish from the current speed.</summary>
    public static TimeSpan? Eta(RunStatus s) =>
        s.Live is { FilesPerMinute: > 0.1 } live && s.NonFinal > 0 ? TimeSpan.FromMinutes(s.NonFinal / live.FilesPerMinute) : null;
}
