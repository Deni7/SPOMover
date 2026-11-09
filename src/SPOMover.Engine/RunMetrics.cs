using System.Collections.Concurrent;
using SPOMover.Spo;

namespace SPOMover.Engine;

/// <summary>Live counters of a run, kept in memory: the dashboard does not query the database for them.</summary>
public sealed class RunMetrics
{
    private long _found;
    private long _foundBefore;
    private readonly ConcurrentDictionary<string, long> _errors = new();
    private readonly DateTimeOffset _started;
    private readonly TimeProvider _time;
    private AppPool? _pool;

    public RunMetrics(TimeProvider time)
    {
        _time = time;
        _started = time.GetUtcNow();
    }

    public long Found => Interlocked.Read(ref _found);

    internal void AttachPool(AppPool pool) => _pool = pool;

    internal void SetFound(long n) => Interlocked.Exchange(ref _found, n);

    internal void AddFound(long n) => Interlocked.Add(ref _found, n);

    /// <summary>Files listed before this start (a continued run): the count goes on from them, the speed counts only new ones.</summary>
    internal void SetFoundBefore(long n)
    {
        Interlocked.Exchange(ref _foundBefore, n);
        Interlocked.Exchange(ref _found, n);
    }

    internal void AddError(string errorClass) => _errors.AddOrUpdate(errorClass, 1, (_, v) => v + 1);

    public MetricsSnapshot Snapshot()
    {
        var elapsed = _time.GetUtcNow() - _started;
        double minutes = Math.Max(elapsed.TotalMinutes, 1.0 / 60);
        return new MetricsSnapshot(
            _time.GetUtcNow(),
            Found,
            (Found - Interlocked.Read(ref _foundBefore)) / minutes,
            _errors.ToDictionary(x => x.Key, x => x.Value),
            _pool?.Apps.Select(a => new AppSnapshot(
                a.Name,
                a.Budget.SpentTotal,
                a.Budget.SpentLast24h,
                a.Budget.WindowLoad,
                a.Budget.Capacity,
                Interlocked.Read(ref a.Throttle.Throttled429),
                Interlocked.Read(ref a.Throttle.Throttled503),
                Interlocked.Read(ref a.Throttle.Requests),
                a.Throttle.IsPaused ? a.Throttle.PausedUntil : null,
                a.Throttle.ConcurrencyLimit,
                a.Throttle.IsBlocked)).ToList() ?? [],
            _pool?.TenantBudget.SpentTotal ?? 0,
            _pool?.TenantBudget.WindowLoad ?? 0,
            _started,
            Interlocked.Read(ref _foundBefore));
    }
}

/// <param name="Errors">Failed requests by error class that the run recovered from or stopped on.</param>
public sealed record MetricsSnapshot(
    DateTimeOffset At,
    long Found,
    double FilesPerMinute,
    IReadOnlyDictionary<string, long> Errors,
    IReadOnlyList<AppSnapshot> Apps,
    long TenantSpent,
    double TenantUtilization,
    DateTimeOffset? Started = null,
    long FoundBefore = 0);

/// <summary>THR-8: load of one app of the pool.</summary>
public sealed record AppSnapshot(
    string Name,
    long RuSpent,
    long RuLast24h,
    double MinuteUtilization,
    double MinuteCapacity,
    long Throttled429,
    long Throttled503,
    long Requests,
    DateTimeOffset? PausedUntil,
    int ConcurrencyLimit,
    bool Blocked);
