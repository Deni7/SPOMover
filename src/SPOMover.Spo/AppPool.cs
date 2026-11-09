using System.Net;
using Microsoft.Extensions.Logging;
using SPOMover.Core.Config;
using SPOMover.Spo.Auth;
using SPOMover.Spo.Graph;
using SPOMover.Spo.Throttling;
using SPOMover.Core.Localization;

namespace SPOMover.Spo;

/// <summary>Per-run settings of every app of the pool.</summary>
public sealed record SpoAppSettings(
    int MaxConcurrency,
    int RequestAttempts,
    TimeSpan RequestTimeout,
    TimeSpan BlockedAfter,
    double BudgetScale);

/// <summary>One app registration with its own token, RU budget and throttle state (THR-9).</summary>
public sealed class SpoApp
{
    private int _activeFiles;

    public SpoApp(string name, GraphClient client, RuBudget budget, ThrottleState throttle)
    {
        Name = name;
        Client = client;
        Budget = budget;
        Throttle = throttle;
    }

    public string Name { get; }

    public GraphClient Client { get; }

    public RuBudget Budget { get; }

    public ThrottleState Throttle { get; }

    public int ActiveFiles => Volatile.Read(ref _activeFiles);

    internal void EnterFile() => Interlocked.Increment(ref _activeFiles);

    public void LeaveFile() => Interlocked.Decrement(ref _activeFiles);
}

public interface ISpoAppFactory
{
    SpoApp Create(Connection connection, SpoAppSettings settings, RuBudget tenantBudget);
}

/// <summary>Real apps: MSAL token provider and an HttpClient to graph.microsoft.com.</summary>
public sealed class SpoAppFactory(ILoggerFactory loggers, TimeProvider? time = null) : ISpoAppFactory
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public SpoApp Create(Connection connection, SpoAppSettings settings, RuBudget tenantBudget)
    {
        var logger = loggers.CreateLogger("Spo." + connection.Name);
        var (perMinute, perDay, _) = connection.Limits();
        var budget = new RuBudget(connection.Name, perMinute, TimeSpan.FromMinutes(1), perDay, _time) { Scale = settings.BudgetScale };
        var throttle = new ThrottleState(connection.Name, settings.MaxConcurrency, settings.BlockedAfter, _time);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = Math.Max(4, settings.MaxConcurrency * 2),
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new GraphClient(connection.Name, http, TokenProviderFactory.Create(connection, logger), budget, tenantBudget, throttle,
            new GraphClientOptions { RequestAttempts = settings.RequestAttempts, RequestTimeout = settings.RequestTimeout }, logger, _time,
            TokenProviderFactory.SharePoint(connection));
        return new SpoApp(connection.Name, client, budget, throttle);
    }
}

/// <summary>
/// THR-9, THR-10: apps of one run over a shared tenant budget. A file is given to the app with free capacity;
/// an app that is paused by throttling gets no new files while others can take them.
/// </summary>
public sealed class AppPool
{
    private readonly SpoApp[] _apps;

    public AppPool(IReadOnlyList<SpoApp> apps, RuBudget tenantBudget)
    {
        if (apps.Count == 0)
            throw new ArgumentException(L.T("A pool without apps."), nameof(apps));
        _apps = apps.ToArray();
        TenantBudget = tenantBudget;
    }

    public IReadOnlyList<SpoApp> Apps => _apps;

    /// <summary>Enumeration (delta) always goes through one app so the delta token stays consistent.</summary>
    public SpoApp Primary => _apps[0];

    public RuBudget TenantBudget { get; }

    public static AppPool Create(ISpoAppFactory factory, IReadOnlyList<Connection> connections, SpoAppSettings settings,
        int tenantCeilingPercent, TimeProvider? time = null)
    {
        // THR-10: the tenant limit is the same for every app of the tenant; take the smallest tier configured.
        int tenantPer5Min = connections.Min(c => c.Limits().TenantPer5Min);
        var tenant = new RuBudget("tenant", tenantPer5Min * tenantCeilingPercent / 100.0, TimeSpan.FromMinutes(5), 0, time);
        return new AppPool(connections.Select(c => factory.Create(c, settings, tenant)).ToList(), tenant);
    }

    public SpoApp? Find(string? name) => _apps.FirstOrDefault(a => a.Name == name);

    /// <summary>App for the next file: not blocked, not paused if possible, fewest files relative to its budget.</summary>
    public SpoApp Acquire()
    {
        var candidates = _apps.Where(a => !a.Throttle.IsBlocked && !a.Budget.DailyExhausted).ToList();
        if (candidates.Count == 0)
            candidates = _apps.Where(a => !a.Throttle.IsBlocked).ToList();
        if (candidates.Count == 0)
            candidates = [.. _apps];
        var ready = candidates.Where(a => !a.Throttle.IsPaused).ToList();
        var app = (ready.Count > 0 ? ready : candidates)
            .OrderBy(a => ready.Count > 0 ? 0 : a.Throttle.PausedUntil.UtcTicks)
            .ThenBy(a => a.ActiveFiles / Math.Max(0.1, a.Budget.Capacity))
            .ThenBy(a => a.Budget.Utilization)
            .First();
        app.EnterFile();
        return app;
    }

    public bool AllBlocked => _apps.All(a => a.Throttle.IsBlocked);

    /// <summary>THR-6: every app has used its 24-hour budget; the dispatcher pauses instead of starting files.</summary>
    public bool AllDailyExhausted => _apps.All(a => a.Budget.DailyExhausted || a.Throttle.IsBlocked);

    /// <summary>THR-5: budget share of the current time window.</summary>
    public void SetBudgetScale(double scale)
    {
        foreach (var app in _apps)
            app.Budget.Scale = scale;
    }
}
