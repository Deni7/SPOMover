using System.Text.Json;
using Microsoft.Extensions.Logging;
using SPOMover.Core;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Core.Mapping;
using SPOMover.Spo;
using SPOMover.State;

namespace SPOMover.Engine;

/// <summary>
/// EXE-7: after a wave is moved and verified, the source keeps the folders the files left. A cleanup run looks at
/// each of them, deepest first, and sends it to the site recycle bin only if it is empty at that moment; a folder
/// with anything left in it (files that stayed by a rule, a conflict, a lock, new files) is kept and reported.
/// </summary>
public sealed partial class RunEngine
{
    /// <summary>
    /// The folders an apply run may have emptied: every folder above a moved file, up to the folder of the mapping
    /// rule that moved it (never above it, never the library root). Folders outside the mapping are not touched.
    /// </summary>
    public static IReadOnlyList<string> CleanupCandidates(StateStore store, long applyRun)
    {
        var run = store.GetRun(applyRun) ?? throw new KeyNotFoundException(L.T($"Run {applyRun} not found."));
        var set = RunConfig.Parse(run.ConfigSnapshot).Profile.BuildMapping().Set ?? MappingSet.Empty;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in store.MovedPaths(applyRun))
        {
            if (set.Resolve(path) is not { } match)
                continue;
            string top = MappingSet.Key(match.Rule.Source);
            for (string dir = Parent(path); dir.Length > 0 && dir.Length >= top.Length; dir = Parent(dir))
            {
                string key = dir.ToLowerInvariant();
                if (top.Length > 0 && key != top && !key.StartsWith(top + "/", StringComparison.Ordinal))
                    break;
                if (!result.Add(dir))
                    break; // its parents are there already
            }
        }
        return [.. result.OrderByDescending(Depth).ThenBy(p => p, StringComparer.Ordinal)];
    }

    private static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;

    private async Task<string> CleanupAsync(RunContext ctx, CancellationToken stop, CancellationToken abort)
    {
        long runId = ctx.Run.Id;
        string drive = ctx.Targets[0].DriveId!;
        var client = ctx.Pool.Primary.Client;
        using var any = CancellationTokenSource.CreateLinkedTokenSource(stop, abort);
        Metrics.SetFoundBefore(_store.CountFiles(runId, new FileFilter()) - _store.CountNonFinal(runId));
        try
        {
            // Folders of the apply run that moved whole are gone with everything under them: no request for those.
            var gone = new List<string>();
            if (ctx.Run.ParentId is long applyRun)
            {
                foreach (var batch in _store.GetApplyBatches(applyRun).Where(b => b.IsFolder))
                {
                    await WaitWhileHeldAsync(ctx, any.Token).ConfigureAwait(false);
                    if (await client.FindItemByPathAsync(drive, batch.Source, any.Token).ConfigureAwait(false) is null)
                        gone.Add(batch.Source.ToLowerInvariant());
                }
            }
            bool Gone(string path)
            {
                string key = path.ToLowerInvariant();
                return gone.Any(g => key == g || key.StartsWith(g + "/", StringComparison.Ordinal));
            }

            var pending = _store.QueryFiles(runId, new FileFilter { Statuses = [FileStatuses.Pending] }, 0, int.MaxValue);
            int parallel = Math.Max(1, ctx.Profile.Plan.Parallel);
            var nextMetrics = Now + _options.MetricsInterval;
            // Level by level, deepest first: a parent is looked at only after all its children.
            foreach (var level in pending.GroupBy(f => Depth(f.Path!)).OrderByDescending(g => g.Key))
            {
                await Parallel.ForEachAsync(level, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = any.Token }, async (folder, ct) =>
                {
                    await WaitWhileHeldAsync(ctx, ct).ConfigureAwait(false);
                    if (_abortReason is not null)
                        return;
                    if (Gone(folder.Path!))
                    {
                        _store.SetCleanupOutcome(folder.Id, FileStatuses.SkippedNotFound, null, null, L.T("moved together with the folder of the batch"), Now);
                    }
                    else
                    {
                        var (status, errorClass, error, comment) = await CleanFolderAsync(client, drive, folder.Path!, ct).ConfigureAwait(false);
                        _store.SetCleanupOutcome(folder.Id, status, errorClass, error, comment, Now);
                        if (errorClass == "auth")
                            _abortReason ??= L.T($"Deleting folders: access denied ({error}).");
                    }
                    Metrics.AddFound(1);
                }).ConfigureAwait(false);
                if (_abortReason is not null)
                    return Abort(runId, _abortReason);
                if (Now >= nextMetrics)
                {
                    _store.AddMetrics(runId, JsonSerializer.Serialize(Metrics.Snapshot(), Profile.JsonOptions), Now);
                    nextMetrics = Now + _options.MetricsInterval;
                }
            }
        }
        catch (OperationCanceledException) when (any.IsCancellationRequested)
        {
            _store.SetRunStatus(runId, RunStatuses.Stopped, Now, abort.IsCancellationRequested ? L.T("Interrupted") : L.T("Stopped"));
            return RunStatuses.Stopped;
        }
        catch (SpoBlockedException ex)
        {
            return Abort(runId, ex.Message);
        }
        return Complete(runId);
    }

    /// <summary>One folder: deleted if it is empty now, kept with what is in it otherwise.</summary>
    private async Task<(string Status, string? ErrorClass, string? Error, string? Comment)> CleanFolderAsync(Spo.Graph.GraphClient client, string drive, string path, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var children = await client.GetChildrenAsync(drive, path, ct).ConfigureAwait(false);
                if (children.Count > 0)
                {
                    int files = children.Count(c => !c.IsFolder), folders = children.Count - files;
                    return (FileStatuses.SkippedNotEmpty, null, null, L.T($"not empty: files {files}, folders {folders}"));
                }
                try
                {
                    bool deleted = await client.DeleteItemByPathAsync(drive, path, ct).ConfigureAwait(false);
                    return deleted ? (FileStatuses.Deleted, null, null, L.T("in the site recycle bin")) : (FileStatuses.SkippedNotFound, null, null, L.T("already gone"));
                }
                catch (SpoUncertainException)
                {
                    // The answer was lost: the folder decides.
                    bool there = await client.FindItemByPathAsync(drive, path, ct).ConfigureAwait(false) is not null;
                    if (!there)
                        return (FileStatuses.Deleted, null, null, L.T("in the site recycle bin"));
                    throw new SpoException(ErrorClass.Transient, L.T("the answer to the delete was lost, the folder is still there"));
                }
            }
            catch (SpoException ex) when (ex.IsNotFound)
            {
                return (FileStatuses.SkippedNotFound, null, null, L.T("already gone"));
            }
            catch (SpoException ex) when (ex is not SpoBlockedException && ex.ErrorClass == ErrorClass.Transient && attempt < 3)
            {
                Metrics.AddError("transient");
                await Task.Delay(_options.EnumerationRetryDelay, _time, ct).ConfigureAwait(false);
            }
            catch (SpoException ex) when (ex is not SpoBlockedException)
            {
                string cls = ex.ErrorClass switch { ErrorClass.Auth => "auth", ErrorClass.Transient => "transient", ErrorClass.Locked => "locked", _ => "file" };
                _logger.LogWarning("Folder {Path} was not deleted: {Message}", path, ex.Message);
                return (FileStatuses.Failed(cls), cls, ex.Message, null);
            }
        }
    }
}
