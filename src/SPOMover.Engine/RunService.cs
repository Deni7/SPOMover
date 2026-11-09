using Microsoft.Extensions.Logging;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Spo;
using SPOMover.State;

namespace SPOMover.Engine;

public static class SettingKinds
{
    public const string Profile = "profile";
    public const string Connection = "connection";
}

public sealed class RunValidationException(IReadOnlyList<ValidationIssue> issues)
    : Exception(L.T("The profile has errors:") + " " + string.Join("; ", issues.Where(i => i.Severity == IssueSeverity.Error)))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}

/// <summary>
/// The engine's front door for the host API and the GUI: stored settings, creation of runs from profiles,
/// and control of the one active run (start, pause, continue, stop).
/// </summary>
public sealed class RunService : IAsyncDisposable
{
    private readonly StateStore _store;
    private readonly ISpoAppFactory _apps;
    private readonly ILoggerFactory _loggers;
    private readonly EngineOptions _options;
    private readonly object _sync = new();
    private ActiveRun? _active;

    public RunService(StateStore store, ISpoAppFactory apps, ILoggerFactory loggers, EngineOptions options)
    {
        _store = store;
        _apps = apps;
        _loggers = loggers;
        _options = options;
    }

    public StateStore Store => _store;

    private DateTimeOffset Now => _options.Time.GetUtcNow();

    public sealed class ActiveRun(long runId, RunEngine engine, CancellationTokenSource stop, CancellationTokenSource abort)
    {
        public long RunId { get; } = runId;
        public RunEngine Engine { get; } = engine;
        public CancellationTokenSource Stop { get; } = stop;
        public CancellationTokenSource Abort { get; } = abort;
        public Task<string> Completion { get; set; } = Task.FromResult(RunStatuses.Created);
    }

    public ActiveRun? Active
    {
        get
        {
            lock (_sync)
                return _active;
        }
    }

    // ------------------------------------------------------------------ settings

    public IReadOnlyList<Connection> Connections() =>
        _store.ListSettings(SettingKinds.Connection).Select(c => Connection.Parse(c.Json)).ToList();

    public Profile GetProfile(string name) =>
        _store.GetSetting(SettingKinds.Profile, name) is { } json ? Profile.Parse(json) : throw new KeyNotFoundException(L.T($"Profile \"{name}\" not found."));

    /// <summary>Validates a profile as a run would: connections present, limits valid (CFG-6).</summary>
    public ProfileValidation ValidateProfile(Profile profile)
    {
        var used = Connections().Where(c => profile.Connections.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return profile.ValidateForRun(used);
    }

    public sealed record ConnectionCheck(string What, bool Ok, string Message);

    /// <summary>GUI-26: gets a token and, with a profile, checks access to each of its targets.</summary>
    public async Task<IReadOnlyList<ConnectionCheck>> TestConnectionAsync(string name, string? profileName, CancellationToken ct)
    {
        var connection = Connections().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(L.T($"Connection \"{name}\" not found."));
        var checks = new List<ConnectionCheck>();
        var invalid = connection.Validate().Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (invalid.Count > 0)
            return [new ConnectionCheck(L.T("settings"), false, string.Join("; ", invalid.Select(i => i.Message)))];

        SpoApp app;
        try
        {
            var tenant = new Spo.Throttling.RuBudget("tenant", connection.Limits().TenantPer5Min * 0.5, TimeSpan.FromMinutes(5), 0, _options.Time);
            app = _apps.Create(connection, new SpoAppSettings(4, 3, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(15), 0.5), tenant);
        }
        catch (SpoException ex)
        {
            return [new ConnectionCheck(L.T("certificate"), false, ex.Message)];
        }

        var targets = profileName is null ? [] : GetProfile(profileName).Libraries();
        try
        {
            await app.Client.CheckTokenAsync(ct).ConfigureAwait(false);
            checks.Add(new ConnectionCheck(L.T("token"), true, L.T("Token received.")));
        }
        catch (SpoException ex)
        {
            checks.Add(new ConnectionCheck(L.T("token"), false, ex.Message));
            return checks;
        }
        foreach (var target in targets)
        {
            try
            {
                var (drive, _, root) = await TargetResolver.ResolveAsync(app.Client, target, ct).ConfigureAwait(false);
                string folder = root.Length > 0 ? L.T($", folder {root}") : string.Empty;
                checks.Add(new ConnectionCheck(target.ToString(), true, L.T($"Access OK: library {drive.Name}{folder}.")));
            }
            catch (SpoException ex)
            {
                checks.Add(new ConnectionCheck(target.ToString(), false, ex.Message));
            }
        }
        return checks;
    }

    // ------------------------------------------------------------------ runs

    /// <summary>New run of a stored profile (or of a profile given inline) in the given mode.</summary>
    /// <param name="update">
    /// INV-5: an inventory that starts from the last finished inventory of the same source and lists only the changes
    /// since then. Libraries the earlier inventory did not list are listed in full.
    /// </param>
    public long CreateRun(string mode, string? profileName, Profile? inline = null, bool update = false)
    {
        if (!RunModes.All.Contains(mode))
        {
            string modes = string.Join(", ", RunModes.All);
            throw new ArgumentException(L.T($"Unknown mode \"{mode}\" ({modes})."));
        }
        if (mode == RunModes.Cleanup)
            throw new ArgumentException(L.T("Deleting empty folders starts from a move run after verification: \"Delete empty folders…\" on its card, or spo-mover cleanup-folders <run>."));
        if (mode == RunModes.Apply)
            return CreateApplyRun(profileName ?? inline?.Name ?? throw new ArgumentException(L.T("No profile given.")));
        var profile = inline ?? GetProfile(profileName ?? throw new ArgumentException(L.T("No profile given.")));
        var config = Snapshot(profile);
        if (!update)
            return _store.CreateRun(mode, null, profileName ?? profile.Name, config.ToJson(), enumerate: true, config.Profile.Libraries().Count, Now);

        var parent = MappingCheck.LastInventory(_store, profile)
            ?? throw new InvalidOperationException(L.T("Nothing to update: this profile has no finished inventory of the source yet. Run a full inventory."));
        var earlier = RunConfig.Parse(parent.ConfigSnapshot).Profile.Libraries();
        var earlierTargets = _store.GetTargets(parent.Id).ToDictionary(t => t.Index);
        var libraries = config.Profile.Libraries();
        var map = new Dictionary<int, int>();
        for (int i = 0; i < libraries.Count; i++)
        {
            int j = Enumerable.Range(0, earlier.Count).FirstOrDefault(k =>
                string.Equals(earlier[k].Library?.Trim(), libraries[i].Library?.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals((earlier[k].Folder ?? string.Empty).Trim('/'), (libraries[i].Folder ?? string.Empty).Trim('/'), StringComparison.OrdinalIgnoreCase), -1);
            if (j >= 0 && earlierTargets.TryGetValue(j, out var t) && t.Done && t.DeltaLink is not null)
                map[i] = j;
        }
        long id = _store.CreateRun(mode, parent.Id, profileName ?? profile.Name, config.ToJson(), enumerate: true, libraries.Count, Now);
        _store.CopyInventory(parent.Id, id, map);
        return id;
    }

    /// <summary>
    /// Stage 4: an apply run of a plan of the profile (the newest one when <paramref name="planId"/> is null). The plan
    /// decides what moves where; the profile gives connections, limits and retries. Needs a profile without errors.
    /// </summary>
    public long CreateApplyRun(string profileName, long? planId = null)
    {
        var profile = GetProfile(profileName);
        var plan = planId is { } id ? _store.GetPlan(id) ?? throw new KeyNotFoundException(L.T($"Plan {id} not found."))
            : Planner.Latest(_store, profile) ?? throw new InvalidOperationException(L.T("This profile has no plan yet. Build it first: spo-mover plan build."));
        if (!string.Equals(plan.ProfileName, profile.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.T($"Plan {plan.Id} was built for profile \"{plan.ProfileName}\", not \"{profile.Name}\"."));
        var connections = Connections().Where(c => profile.Connections.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var validation = profile.ValidateForRun(connections);
        if (!validation.IsValid)
            throw new RunValidationException(validation.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList());
        var config = new RunConfig { Profile = Profile.Parse(profile.ToJson()), Connections = connections };
        var libraries = config.Profile.Libraries();
        var dest = Enumerable.Range(1, libraries.Count - 1).ToDictionary(i => libraries[i].Library!.Trim(), i => i, StringComparer.OrdinalIgnoreCase);
        return _store.CreateApplyRun(plan.Id, profile.Name, config.ToJson(), libraries.Count, dest, Now);
    }

    /// <summary>What a cleanup of an apply run would look at, and why it may not start yet.</summary>
    public sealed record CleanupPreview(long ApplyRun, int Folders, long Verified, long NotVerified, string? Blocker);

    /// <summary>
    /// EXE-7: may the source folders of this apply run be cleaned up, and how many are there to look at. Only after
    /// verify: a folder is deleted only when it is empty, but a file that verify did not confirm may still be in it.
    /// </summary>
    public CleanupPreview PreviewCleanup(long applyRun)
    {
        var run = _store.GetRun(applyRun) ?? throw new KeyNotFoundException(L.T($"Run {applyRun} not found."));
        if (run.Mode != RunModes.Apply)
            return new(applyRun, 0, 0, 0, L.T($"Run {applyRun} is not a move (apply)."));
        var counts = _store.CountByStatus(applyRun);
        long verified = counts.GetValueOrDefault(FileStatuses.Verified), notVerified = counts.GetValueOrDefault(FileStatuses.Moved);
        string? blocker = !RunStatuses.IsFinished(run.Status) ? L.T($"The move has not finished yet ({run.Status}).")
            : notVerified > 0 ? L.T($"Not everything is verified: {notVerified} moved files are not confirmed yet. First \"Verify…\".")
            : verified == 0 ? L.T("No verified moved files: first \"Verify…\".")
            : null;
        int folders = blocker is null ? RunEngine.CleanupCandidates(_store, applyRun).Count : 0;
        if (blocker is null && folders == 0)
            blocker = L.T("No folders to check: everything was moved as whole folders.");
        return new(applyRun, folders, verified, notVerified, blocker);
    }

    /// <summary>EXE-7: a cleanup run for the source folders an apply run emptied (start it with <see cref="Start"/>).</summary>
    public long CreateCleanupRun(long applyRun)
    {
        var preview = PreviewCleanup(applyRun);
        if (preview.Blocker is not null)
            throw new InvalidOperationException(preview.Blocker);
        var run = _store.GetRun(applyRun)!;
        var profile = RunConfig.Parse(run.ConfigSnapshot).Profile;
        // Today's connections (a certificate may have been renewed since the move), the move's profile and mapping.
        var connections = Connections().Where(c => profile.Connections.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var validation = profile.ValidateForRun(connections);
        if (!validation.IsValidForInventory)
            throw new RunValidationException(validation.Issues.Where(i => i.Area is null).ToList());
        var config = new RunConfig { Profile = profile, Connections = connections };
        return _store.CreateCleanupRun(applyRun, run.ProfileName, config.ToJson(), RunEngine.CleanupCandidates(_store, applyRun), Now);
    }

    /// <summary>
    /// retry (FEATURES 8): chosen failed or deferred files of a finished apply run (by error class or id; none — all)
    /// are moved again: their batches go back to the queue and the run continues. Returns how many files.
    /// </summary>
    public int Retry(long runId, IReadOnlyCollection<string>? errorClasses = null, IReadOnlyCollection<long>? fileIds = null)
    {
        var run = _store.GetRun(runId) ?? throw new KeyNotFoundException(L.T($"Run {runId} not found."));
        if (run.Mode != RunModes.Apply)
            throw new InvalidOperationException(L.T($"Retry is only for a move (apply); run {runId} is {run.Mode}."));
        lock (_sync)
        {
            if (_active is { } current && current.RunId == runId && !current.Completion.IsCompleted)
                throw new InvalidOperationException(L.T($"Run {runId} is running."));
        }
        if (run.Status is RunStatuses.Running or RunStatuses.Paused)
            throw new InvalidOperationException(L.T($"Run {runId} is marked \"{run.Status}\": stop it first."));
        int n = _store.ReopenApply(runId, errorClasses ?? [], fileIds ?? [], Now);
        if (n == 0)
            throw new InvalidOperationException(L.T("No files to retry with these conditions."));
        return n;
    }

    private RunConfig Snapshot(Profile profile)
    {
        var connections = Connections().Where(c => profile.Connections.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var validation = profile.ValidateForRun(connections);
        // Stage 2 has the inventory only; it runs with an unfinished mapping (see ProfileValidation.IsValidForInventory).
        if (!validation.IsValidForInventory)
            throw new RunValidationException(validation.Issues.Where(i => i.Area is null).ToList());
        return new RunConfig { Profile = Profile.Parse(profile.ToJson()), Connections = connections };
    }

    /// <summary>Starts or continues a run in the background. Only one run is active at a time: they share app budgets.</summary>
    public ActiveRun Start(long runId)
    {
        var run = _store.GetRun(runId) ?? throw new KeyNotFoundException(L.T($"Run {runId} not found."));
        if (!RunStatuses.CanContinue(run.Status))
            throw new InvalidOperationException(L.T($"Run {runId} has already finished ({run.Status})."));
        lock (_sync)
        {
            if (_active is { } current && !current.Completion.IsCompleted)
            {
                if (current.RunId == runId)
                    return current;
                throw new InvalidOperationException(L.T($"Run {current.RunId} is already running. Stop it or wait for it to finish."));
            }
            var engine = new RunEngine(_store, _apps, _loggers.CreateLogger($"Run.{runId}"), _options);
            var active = new ActiveRun(runId, engine, new CancellationTokenSource(), new CancellationTokenSource());
            active.Completion = Task.Run(async () =>
            {
                try
                {
                    return await engine.RunAsync(runId, active.Stop.Token, active.Abort.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _loggers.CreateLogger("RunService").LogError(ex, "Run {Run} crashed", runId);
                    _store.SetRunStatus(runId, RunStatuses.Aborted, Now, L.T("Internal error:") + " " + ex.Message);
                    return RunStatuses.Aborted;
                }
            });
            _active = active;
            return active;
        }
    }

    /// <summary>
    /// Deletes a run that is not running: its files, results and listing. The list of runs and the reports no longer show it.
    /// </summary>
    public void DeleteRun(long runId)
    {
        RunRow run;
        lock (_sync)
        {
            run = _store.GetRun(runId) ?? throw new KeyNotFoundException(L.T($"Run {runId} not found."));
            if (_active is { } current && current.RunId == runId && !current.Completion.IsCompleted)
                throw new InvalidOperationException(L.T($"Run {runId} is running. Stop it, wait until it stops, then delete it."));
            // Marked running or paused but not active: the host was killed, and the run continues on the next start (STA-3).
            if (run.Status is RunStatuses.Running or RunStatuses.Paused)
                throw new InvalidOperationException(L.T($"Run {runId} is marked \"{run.Status}\": it will continue after a restart. Stop it first."));
            _store.DeleteRun(runId);
        }
        _loggers.CreateLogger("RunService").LogWarning("Run {Run} ({Mode}, profile {Profile}, {Status}) deleted", runId, run.Mode, run.ProfileName, run.Status);
    }

    /// <summary>
    /// STA-3: after the host was killed, a run still marked running (or paused) is continued from where it stopped.
    /// </summary>
    public long? ContinueInterrupted()
    {
        var run = _store.ListRuns(200).Where(r => r.Status is RunStatuses.Running or RunStatuses.Paused).OrderBy(r => r.Id).LastOrDefault();
        if (run is null)
            return null;
        _loggers.CreateLogger("RunService").LogWarning("Continuing interrupted run {Run}", run.Id);
        Start(run.Id);
        return run.Id;
    }

    public void Pause(long runId)
    {
        var active = Require(runId);
        active.Engine.Pause();
        _store.SetRunStatusIf(runId, RunStatuses.Paused, Now, RunStatuses.Running);
    }

    public void Resume(long runId)
    {
        var active = Require(runId);
        active.Engine.Resume();
        _store.SetRunStatusIf(runId, RunStatuses.Running, Now, RunStatuses.Paused);
    }

    /// <summary>STA-4: graceful stop; the run can be continued later with <see cref="Start"/>.</summary>
    public void Stop(long runId) => Require(runId).Stop.Cancel();

    private ActiveRun Require(long runId)
    {
        var active = Active;
        if (active is null || active.RunId != runId || active.Completion.IsCompleted)
            throw new InvalidOperationException(L.T($"Run {runId} is not running now."));
        return active;
    }

    /// <summary>Host shutdown: graceful stop, then a hard stop if the run does not stop in time.</summary>
    public async ValueTask DisposeAsync()
    {
        var active = Active;
        if (active is null || active.Completion.IsCompleted)
            return;
        active.Stop.Cancel();
        if (await Task.WhenAny(active.Completion, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != active.Completion)
            active.Abort.Cancel();
        try
        {
            await active.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
