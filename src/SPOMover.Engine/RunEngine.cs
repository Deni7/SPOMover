using System.Text.Json;
using Microsoft.Extensions.Logging;
using SPOMover.Core;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Spo;
using SPOMover.Spo.Graph;
using SPOMover.State;

namespace SPOMover.Engine;

public sealed class EngineOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How often the run loop checks the listing, stop requests and the time window.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often metrics are written to run_metrics for charts.</summary>
    public TimeSpan MetricsInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Pause between attempts to continue a failed enumeration.</summary>
    public TimeSpan EnumerationRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Runs one run to a final status (RTY-7) or until it is stopped. Everything it does is recorded in the state
/// store, so a run interrupted at any point continues from the same place (P-6, STA-3).
/// Stage 1: the inventory mode — every library of the profile is listed through Graph delta (INV-1).
/// </summary>
public sealed partial class RunEngine
{
    private readonly StateStore _store;
    private readonly ISpoAppFactory _apps;
    private readonly ILogger _logger;
    private readonly EngineOptions _options;
    private readonly TimeProvider _time;
    private volatile bool _paused;
    private volatile string? _abortReason;

    public RunEngine(StateStore store, ISpoAppFactory apps, ILogger logger, EngineOptions options)
    {
        _store = store;
        _apps = apps;
        _logger = logger;
        _options = options;
        _time = options.Time;
        Metrics = new RunMetrics(_time);
    }

    public RunMetrics Metrics { get; }

    public bool IsPaused => _paused;

    /// <summary>STA-5: no new pages are requested; the page in progress finishes.</summary>
    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <param name="stop">STA-4: graceful stop — finish the current page, save state.</param>
    /// <param name="abort">Hard stop: the current page is interrupted and requested again on the next start.</param>
    /// <returns>Final run status.</returns>
    public async Task<string> RunAsync(long runId, CancellationToken stop, CancellationToken abort = default)
    {
        var run = _store.GetRun(runId) ?? throw new InvalidOperationException(L.T($"Run {runId} not found."));
        var config = RunConfig.Parse(run.ConfigSnapshot);
        var profile = config.Profile;
        var validation = profile.ValidateForRun(config.Connections);
        bool apply = run.Mode == RunModes.Apply;
        if (apply ? !validation.IsValid : !validation.IsValidForInventory)
        {
            string reason = L.T("The settings snapshot is invalid:") + " " + string.Join("; ", validation.Issues.Where(i => i.Severity == IssueSeverity.Error && (apply || i.Area is null)));
            _store.SetRunStatus(runId, RunStatuses.Aborted, Now, reason);
            return RunStatuses.Aborted;
        }

        var ctx = new RunContext(run, config);
        var connections = profile.Connections
            .Select(n => config.Connections.First(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var settings = new SpoAppSettings(
            profile.Performance.Concurrency,
            profile.Retry.RequestAttempts,
            TimeSpan.FromSeconds(profile.Performance.RequestTimeoutSeconds),
            TimeSpan.FromMinutes(profile.Retry.BlockedMinutes),
            profile.Performance.BudgetPercent / 100.0);
        try
        {
            ctx.Pool = AppPool.Create(_apps, connections, settings, profile.Pool.TenantCeilingPercent, _time);
        }
        catch (SpoException ex)
        {
            return Abort(runId, L.T($"Connection: {ex.Message}"));
        }
        Metrics.AttachPool(ctx.Pool);

        _store.SetRunStatus(runId, RunStatuses.Running, Now);
        int recovered = _store.RecoverInProgress(runId, Now);
        if (recovered > 0)
            _logger.LogInformation("Run {Run}: {Count} files returned to the queue after interrupted processing", runId, recovered);
        long found = _store.CountFiles(runId, new FileFilter());
        Metrics.SetFoundBefore(found);

        try
        {
            await PrepareTargetsAsync(ctx, abort).ConfigureAwait(false);
        }
        catch (SpoException ex)
        {
            return Abort(runId, L.T($"Preparing targets: {ex.Message}"));
        }

        if (run.Mode == RunModes.Cleanup)
        {
            try
            {
                return await CleanupAsync(ctx, stop, abort).ConfigureAwait(false);
            }
            finally
            {
                _store.AddMetrics(runId, JsonSerializer.Serialize(Metrics.Snapshot(), Profile.JsonOptions), Now);
            }
        }
        if (apply)
        {
            try
            {
                return await ApplyAsync(ctx, stop, abort).ConfigureAwait(false);
            }
            finally
            {
                _store.AddMetrics(runId, JsonSerializer.Serialize(Metrics.Snapshot(), Profile.JsonOptions), Now);
            }
        }

        using var enumerationStop = CancellationTokenSource.CreateLinkedTokenSource(stop, abort);
        var enumeration = run.Enumerate && _store.GetTargets(runId).Any(t => !t.Done || (t.Index == 0 && t.Checkouts == CheckoutScan.Pending))
            ? Task.Run(() => EnumerateAsync(ctx, enumerationStop.Token), CancellationToken.None)
            : Task.CompletedTask;

        string status;
        try
        {
            status = await WatchAsync(ctx, enumeration, stop, abort).ConfigureAwait(false);
        }
        finally
        {
            enumerationStop.Cancel();
            try
            {
                await enumeration.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException || enumeration.IsFaulted)
            {
                // Already turned into the stop reason by the run loop.
            }
            _store.AddMetrics(runId, JsonSerializer.Serialize(Metrics.Snapshot(), Profile.JsonOptions), Now);
        }
        return status;
    }

    private string Abort(long runId, string reason)
    {
        _logger.LogError("Run {Run} aborted: {Reason}", runId, reason);
        _store.SetRunStatus(runId, RunStatuses.Aborted, Now, reason);
        // RU spent on resolving targets count too.
        _store.AddMetrics(runId, JsonSerializer.Serialize(Metrics.Snapshot(), Profile.JsonOptions), Now);
        return RunStatuses.Aborted;
    }

    private sealed class RunContext(RunRow run, RunConfig config)
    {
        public RunRow Run { get; } = run;
        public RunConfig Config { get; } = config;
        public Profile Profile => Config.Profile;
        public AppPool Pool { get; set; } = null!;
        public volatile bool EnumerationDone;
        public string? EnumerationError;
        public Dictionary<int, TargetRow> Targets { get; } = [];
    }

    // ------------------------------------------------------------------ targets

    /// <summary>Resolves site, library and folder of each target and checks that every app of the pool can reach them (THR-11).</summary>
    private async Task PrepareTargetsAsync(RunContext ctx, CancellationToken ct)
    {
        var libraries = ctx.Profile.Libraries();
        var rows = _store.GetTargets(ctx.Run.Id);
        for (int i = 0; i < libraries.Count && i < rows.Count; i++)
        {
            var target = libraries[i];
            var row = rows[i];
            if (row.DriveId is null)
            {
                var (drive, rootItem, rootPath) = await TargetResolver.ResolveAsync(ctx.Pool.Primary.Client, target, ct).ConfigureAwait(false);
                _store.SetTargetDrive(ctx.Run.Id, i, drive.Id, drive.WebUrl, rootItem, rootPath);
            }
        }
        foreach (var row in _store.GetTargets(ctx.Run.Id))
            ctx.Targets[row.Index] = row;

        foreach (var app in ctx.Pool.Apps)
        {
            foreach (var (target, row) in libraries.Zip(ctx.Targets.Values.OrderBy(t => t.Index)))
            {
                try
                {
                    var (drive, _, _) = await TargetResolver.ResolveAsync(app.Client, target, ct).ConfigureAwait(false);
                    if (drive.Id != row.DriveId)
                        throw new SpoException(ErrorClass.Internal, L.T($"the library resolved differently ({drive.Id} instead of {row.DriveId})."));
                }
                catch (SpoException ex)
                {
                    throw new SpoException(ex.ErrorClass, L.T($"app \"{app.Name}\" has no access to target {target}: {ex.Message}"), ex.Status, ex.Code, ex.RequestId, ex);
                }
            }
        }
    }

    // ------------------------------------------------------------------ enumeration (INV-1)

    private async Task EnumerateAsync(RunContext ctx, CancellationToken ct)
    {
        var folders = new Dictionary<(string Drive, string Item), FolderRow>();
        foreach (var f in _store.GetFolders(ctx.Run.Id))
            folders[(f.DriveId, f.ItemId)] = f;

        // The totals of all libraries first, so the progress covers the whole run from its start.
        foreach (var target in _store.GetTargets(ctx.Run.Id).Where(t => !t.Done && t.NextLink is null && t.DeltaLink is null && !t.Incremental))
            await CountItemsAsync(ctx, target, ct).ConfigureAwait(false);

        foreach (var target in _store.GetTargets(ctx.Run.Id).Where(t => !t.Done))
        {
            // INV-5: an update lists only what changed since the earlier inventory (the copied rows are the base).
            int changedItems = 0, deletedItems = 0;
            int failures = 0;
            string? link = target.NextLink;
            // All pages were saved before an interruption; only the final step is left.
            bool done = link is null && target.DeltaLink is not null;
            while (!done)
            {
                await WaitWhileHeldAsync(ctx, ct).ConfigureAwait(false);
                DeltaPage page;
                try
                {
                    page = await ctx.Pool.Primary.Client.GetDeltaPageAsync(target.DriveId!, link, ct).ConfigureAwait(false);
                    failures = 0;
                }
                catch (SpoException ex) when (ex is not SpoBlockedException && ex.ErrorClass == ErrorClass.Transient && ++failures <= 5)
                {
                    _logger.LogWarning("Listing target {Target}: {Message}; continuing from the same position", target.Index, ex.Message);
                    Metrics.AddError("transient");
                    await Task.Delay(_options.EnumerationRetryDelay, _time, ct).ConfigureAwait(false);
                    continue;
                }
                catch (SpoException ex)
                {
                    ctx.EnumerationError = L.T($"Listing files of target {target.Index + 1}: {ex.Message}");
                    _abortReason ??= ctx.EnumerationError;
                    return;
                }

                var newFolders = page.Items
                    .Where(i => (i.IsFolder || i.IsRoot) && !i.Deleted)
                    .Select(i => new FolderRow(target.DriveId!, i.Id, i.ParentId, i.Name, i.IsRoot))
                    .ToList();
                foreach (var f in newFolders)
                    folders[(f.DriveId, f.ItemId)] = f;
                var deleted = page.Items.Where(i => i.Deleted).Select(i => i.Id).ToList();
                if (target.Incremental)
                {
                    changedItems += page.Items.Count(i => !i.Deleted && !i.IsRoot);
                    deletedItems += deleted.Count;
                    foreach (string id in deleted)
                        folders.Remove((target.DriveId!, id));
                }

                var files = new List<FileRow>();
                foreach (var item in page.Items.Where(i => i.IsFile && !i.Deleted))
                {
                    string? dir = FolderPath(folders, target.DriveId!, item.ParentId);
                    var row = new FileRow
                    {
                        RunId = ctx.Run.Id,
                        TargetIndex = target.Index,
                        DriveId = target.DriveId!,
                        ItemId = item.Id,
                        ParentId = item.ParentId,
                        Name = item.Name,
                        Size = item.Size,
                        ETag = item.ETag,
                        Modified = item.Modified,
                        ModifiedBy = item.ModifiedBy,
                        CheckedOutBy = item.CheckedOutBy,
                        Status = FileStatuses.WaitingPath,
                    };
                    if (dir is not null)
                    {
                        string path = Combine(dir, item.Name);
                        if (!InsideTarget(target, path))
                            continue;
                        row = row with { Path = path, Status = FileStatuses.Listed };
                    }
                    files.Add(row);
                }

                done = page.NextLink is null;
                if (target.Incremental && deleted.Count > 0)
                    _store.DeleteItems(ctx.Run.Id, target.DriveId!, deleted);
                int saved = _store.SaveDeltaPage(ctx.Run.Id, target.Index, newFolders, files, page.NextLink, page.DeltaLink, done: false, Now);
                // Counting all rows after every page would cost minutes on a million files; the exact count follows at the end of the library.
                Metrics.AddFound(saved);
                link = page.NextLink;
            }

            // Files that came before their folder.
            foreach (var row in _store.GetWaitingPath(ctx.Run.Id).Where(r => r.TargetIndex == target.Index))
            {
                string? dir = FolderPath(folders, row.DriveId, row.ParentId);
                string? path = dir is null ? null : Combine(dir, row.Name);
                if (path is null)
                    _store.ResolvePath(row.Id, null, FileStatuses.Failed("internal"), L.T("Could not determine the folder of the file from delta."), Now);
                else if (!InsideTarget(target, path))
                    _store.DeleteFile(row.Id); // outside the target folder, same as files placed during the listing
                else
                    _store.ResolvePath(row.Id, path, FileStatuses.Listed, null, Now);
            }
            if (target.Incremental)
                ApplyFolderChanges(ctx, target, folders, changedItems, deletedItems);
            SaveFolderStats(ctx.Run.Id, target, folders);
            _store.SaveDeltaPage(ctx.Run.Id, target.Index, [], [], null, null, done: true, Now);
            Metrics.SetFound(_store.CountFiles(ctx.Run.Id, new FileFilter()));
            _logger.LogInformation("Listing of target {Target} finished", target.Index + 1);
        }

        // INV-3: files without a published version of the source: the listing does not return them.
        var source = _store.GetTargets(ctx.Run.Id).FirstOrDefault(t => t.Index == 0);
        if (source is { Checkouts: CheckoutScan.Pending, DriveId: not null })
            await ScanCheckoutsAsync(ctx, source, ct).ConfigureAwait(false);
        ctx.EnumerationDone = true;
    }

    /// <summary>
    /// The listing progress needs a total that Graph delta does not give: before a library is listed, SharePoint is
    /// asked how many items its list has. Only for a whole library (a folder of it has fewer); without the number the
    /// dashboard shows the listing without a percentage, so any failure here is only logged.
    /// </summary>
    private async Task CountItemsAsync(RunContext ctx, TargetRow target, CancellationToken ct)
    {
        var libraries = ctx.Profile.Libraries();
        if (target.Index >= libraries.Count || !string.IsNullOrEmpty(libraries[target.Index].Folder?.Trim('/')) || target.DriveId is null)
            return;
        try
        {
            await WaitWhileHeldAsync(ctx, ct).ConfigureAwait(false);
            var client = ctx.Pool.Primary.Client;
            string listId = await client.GetListIdAsync(target.DriveId, ct).ConfigureAwait(false);
            long items = await client.GetListItemCountAsync(libraries[target.Index].SiteUrl!, listId, ct).ConfigureAwait(false);
            _store.SetExpectedItems(ctx.Run.Id, target.Index, items);
        }
        catch (SpoException ex) when (ex is not SpoBlockedException)
        {
            _logger.LogInformation("Target {Target}: the number of library items is unknown ({Message}); listing progress has no percentage", target.Index + 1, ex.Message);
        }
    }

    /// <summary>
    /// INV-5: after the changes of a library are applied. Delta reports a renamed or moved folder, but not the files
    /// under it, and a deleted folder may come without its children. So every file's path is checked against the
    /// folder tree once, at the end (also right after a resumed update): changed paths are rewritten, files whose folder
    /// is gone or now outside the listed folder leave the run, and folders that lost their parent are dropped.
    /// </summary>
    private void ApplyFolderChanges(RunContext ctx, TargetRow target, Dictionary<(string Drive, string Item), FolderRow> folders, int changedItems, int deletedItems)
    {
        string drive = target.DriveId!;
        var orphans = folders.Values.Where(f => f.DriveId == drive && !f.IsRoot && FolderPath(folders, drive, f.ParentId) is null).Select(f => f.ItemId).ToList();
        foreach (string id in orphans)
            folders.Remove((drive, id));
        if (orphans.Count > 0)
            _store.DeleteItems(ctx.Run.Id, drive, orphans);

        var dirs = new Dictionary<string, string?>(StringComparer.Ordinal);
        var changes = new List<(long, string?)>();
        int gone = 0;
        foreach (var (id, parent, name, path) in _store.FileNames(ctx.Run.Id, target.Index))
        {
            string? dir = null;
            if (parent is not null && !dirs.TryGetValue(parent, out dir))
                dirs[parent] = dir = FolderPath(folders, drive, parent);
            string? now = dir is null ? null : Combine(dir, name);
            if (now is not null && !InsideTarget(target, now))
                now = null;
            if (now == path)
                continue;
            changes.Add((id, now));
            if (now is null)
                gone++;
        }
        _store.SetPaths(changes, Now);
        _store.AddNote(ctx.Run.Id, "info",
            L.T($"Library {target.Index + 1} updated only with the changes since run {ctx.Run.ParentId}: changed or added {changedItems}, deleted {deletedItems + gone}, paths recalculated {changes.Count - gone}."), Now);
        _logger.LogInformation("Update of target {Target}: changed {Changed}, deleted {Deleted}, paths recalculated {Paths}",
            target.Index + 1, changedItems, deletedItems + gone, changes.Count - gone);
    }

    /// <summary>
    /// INV-2: files, size and the longest path of every folder's subtree, from the listed files and folders.
    /// Written once per library, at the end of its listing (re-done if the run is continued after a stop at that point).
    /// </summary>
    private void SaveFolderStats(long runId, TargetRow target, Dictionary<(string Drive, string Item), FolderRow> folders)
    {
        var stats = new Dictionary<string, (int Depth, long Files, long Bytes, long Direct, int MaxPath)>(StringComparer.Ordinal);
        foreach (var folder in folders.Values.Where(f => f.DriveId == target.DriveId))
        {
            string? path = folder.IsRoot ? string.Empty : FolderPath(folders, folder.DriveId, folder.ParentId) is { } dir ? Combine(dir, folder.Name) : null;
            if (path is not null && (path.Length == 0 || InsideTarget(target, path)))
                stats.TryAdd(path, (Depth(path), 0, 0, 0, 0));
        }
        stats.TryAdd(string.Empty, (0, 0, 0, 0, 0));
        foreach (var (path, size) in _store.FilePaths(runId, target.Index))
        {
            int slash = path.LastIndexOf('/');
            string dir = slash < 0 ? string.Empty : path[..slash];
            bool direct = true;
            while (true)
            {
                stats.TryGetValue(dir, out var s);
                stats[dir] = (Depth(dir), s.Files + 1, s.Bytes + size, s.Direct + (direct ? 1 : 0), Math.Max(s.MaxPath, path.Length));
                direct = false;
                if (dir.Length == 0)
                    break;
                int up = dir.LastIndexOf('/');
                dir = up < 0 ? string.Empty : dir[..up];
            }
        }
        _store.SaveFolderStats(runId, target.Index,
            stats.Select(x => new StateStore.FolderStat(target.Index, x.Key, x.Value.Depth, x.Value.Files, x.Value.Bytes, x.Value.Direct, x.Value.MaxPath)));
    }

    private static int Depth(string path) => path.Length == 0 ? 0 : path.Count(c => c == '/') + 1;

    /// <summary>
    /// INV-3: files without a published version (REST GetCheckedOutFiles). Checked-out files are already known from the
    /// listing. SharePoint gives this list only with fullcontrol on the site (API_SPIKE.md, result 18); without it, or
    /// without the SharePoint Sites.Selected permission, the step is skipped with a note and the inventory completes.
    /// </summary>
    private async Task ScanCheckoutsAsync(RunContext ctx, TargetRow source, CancellationToken ct)
    {
        var client = ctx.Pool.Primary.Client;
        string siteUrl = ctx.Profile.Source!.SiteUrl!.TrimEnd('/');
        string libraryPath = Uri.UnescapeDataString(new Uri(source.DriveUrl!).AbsolutePath).TrimEnd('/');
        try
        {
            await WaitWhileHeldAsync(ctx, ct).ConfigureAwait(false);
            string listId = await client.GetListIdAsync(source.DriveId!, ct).ConfigureAwait(false);
            var unpublished = new List<(string, string?)>();
            foreach (var item in await client.GetUnpublishedFilesAsync(siteUrl, listId, ct).ConfigureAwait(false))
            {
                string decoded = Uri.UnescapeDataString(item.ServerRelativeUrl);
                if (decoded.StartsWith(libraryPath + "/", StringComparison.OrdinalIgnoreCase) && decoded[(libraryPath.Length + 1)..] is { } path && InsideTarget(source, path))
                    unpublished.Add((path, item.By));
            }
            _store.SaveUnpublished(ctx.Run.Id, 0, unpublished);
            _store.SetCheckoutState(ctx.Run.Id, 0, CheckoutScan.Done);
            _logger.LogInformation("Run {Run}: files without a published version: {Count}", ctx.Run.Id, unpublished.Count);
        }
        catch (SpoException ex) when (ex is not SpoBlockedException && ex.ErrorClass is ErrorClass.Auth or ErrorClass.File or ErrorClass.Transient)
        {
            _store.SetCheckoutState(ctx.Run.Id, 0, CheckoutScan.Skipped);
            string why = ex.Status == System.Net.HttpStatusCode.Forbidden
                ? L.T("SharePoint gives this list only with the fullcontrol role on the site (with the write role — 403)")
                : ex.ErrorClass == ErrorClass.Auth
                    ? L.T("the app has not been granted access to SharePoint REST (SharePoint permission → Sites.Selected in Entra, help 1.5)")
                    : ex.Message;
            _store.AddNote(ctx.Run.Id, "warning",
                L.T($"Files without a published version were not checked: {why}. Checked-out files were found from the listing; the listing is complete."), Now);
            _logger.LogWarning("Run {Run}: files without a published version were not checked: {Message}", ctx.Run.Id, ex.Message);
        }
    }

    /// <summary>STA-5, THR-5, THR-6: waits while the run is paused, outside its time windows or out of daily budget.</summary>
    private async Task WaitWhileHeldAsync(RunContext ctx, CancellationToken ct)
    {
        bool logged = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int? budget = ctx.Profile.Performance.BudgetAt(Now);
            string? why = _paused ? null
                : budget is null ? "outside the working windows"
                : ctx.Pool.AllDailyExhausted ? "the daily RU budget is used up"
                : null;
            if (!_paused && why is null)
            {
                ctx.Pool.SetBudgetScale(budget!.Value / 100.0);
                return;
            }
            if (why is not null && !logged)
            {
                _logger.LogInformation("Run {Run}: {Reason}, waiting", ctx.Run.Id, why);
                logged = true;
            }
            await Task.Delay(_paused ? _options.PollInterval : TimeSpan.FromSeconds(30), _time, ct).ConfigureAwait(false);
        }
    }

    private static bool InsideTarget(TargetRow target, string path)
    {
        string root = target.RootPath ?? string.Empty;
        return root.Length == 0 || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FolderPath(Dictionary<(string, string), FolderRow> folders, string drive, string? parentId)
    {
        var parts = new List<string>();
        string? id = parentId;
        for (int depth = 0; depth < 400; depth++)
        {
            if (id is null || !folders.TryGetValue((drive, id), out var folder))
                return null;
            if (folder.IsRoot)
            {
                parts.Reverse();
                return string.Join('/', parts);
            }
            parts.Add(folder.Name);
            id = folder.ParentId;
        }
        return null;
    }

    private static string Combine(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;

    // ------------------------------------------------------------------ run loop

    /// <summary>Waits for the listing while writing metrics; turns a stop, a block or a failure into the final status.</summary>
    private async Task<string> WatchAsync(RunContext ctx, Task enumeration, CancellationToken stop, CancellationToken abort)
    {
        long runId = ctx.Run.Id;
        var nextMetrics = Now + _options.MetricsInterval;
        using var any = CancellationTokenSource.CreateLinkedTokenSource(stop, abort);
        while (true)
        {
            if (abort.IsCancellationRequested)
            {
                _store.SetRunStatus(runId, RunStatuses.Stopped, Now, L.T("Interrupted"));
                return RunStatuses.Stopped;
            }
            if (ctx.Pool.AllBlocked)
                _abortReason ??= new SpoBlockedException(ctx.Pool.Primary.Name, TimeSpan.FromMinutes(ctx.Profile.Retry.BlockedMinutes)).Message;
            if (enumeration.IsFaulted)
                _abortReason ??= L.T("Listing files:") + " " + (enumeration.Exception?.GetBaseException().Message ?? L.T("unknown error"));
            if (_abortReason is not null)
                return Abort(runId, _abortReason);
            if (stop.IsCancellationRequested)
            {
                _store.SetRunStatus(runId, RunStatuses.Stopped, Now, L.T("Stopped"));
                return RunStatuses.Stopped;
            }

            if (Now >= nextMetrics)
            {
                _store.AddMetrics(runId, JsonSerializer.Serialize(Metrics.Snapshot(), Profile.JsonOptions), Now);
                nextMetrics = Now + _options.MetricsInterval;
            }

            if (enumeration.IsCompletedSuccessfully)
            {
                if (ctx.EnumerationError is not null)
                    return Abort(runId, ctx.EnumerationError);
                if (_store.CountNonFinal(runId) == 0)
                    return Complete(runId);
            }

            // While listing, wake up as soon as it ends; afterwards only files waiting in the queue are left.
            var delay = Task.Delay(_options.PollInterval, _time, any.Token);
            await Task.WhenAny(enumeration.IsCompleted ? delay : Task.WhenAny(enumeration, delay)).ConfigureAwait(false);
        }
    }

    private string Complete(long runId)
    {
        var counts = _store.CountByStatus(runId);
        bool errors = counts.Keys.Any(k => k.StartsWith("failed:", StringComparison.Ordinal));
        string status = errors ? RunStatuses.CompletedWithErrors : RunStatuses.Completed;
        _store.SetRunStatus(runId, status, Now);
        _logger.LogInformation("Run {Run} finished: {Status}", runId, status);
        return status;
    }
}
