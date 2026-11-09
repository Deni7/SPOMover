using System.Text.Json;
using Microsoft.Extensions.Logging;
using SPOMover.Core;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Spo;
using SPOMover.State;
using static SPOMover.State.StateStore;

namespace SPOMover.Engine;

/// <summary>
/// Stage 4: the apply mode. Creates the target folders of the plan (EXE-3), then moves its batches with
/// SP.MoveCopyUtil (EXE-1), up to <see cref="PlanOptions.Parallel"/> at a time (EXE-6). Every batch is checked after
/// the move (EXE-5): what is left in the source is moved item by item into the same target. A batch tried before
/// (after a failure, a stop or a crash) starts by looking at source and target instead of moving blindly (EXE-8).
/// </summary>
public sealed partial class RunEngine
{
    private int _authFailures;

    private sealed record Library(string DriveId, string BaseUrl);

    /// <summary>
    /// The target check (columns, content types, settings) once per run, as notes on the run card: the move goes on
    /// whatever it finds (the person starting it saw the same in the dialog), but the run keeps the record.
    /// </summary>
    private async Task NoteTargetCheckAsync(RunContext ctx, CancellationToken ct)
    {
        var notes = _store.GetNotes(ctx.Run.Id);
        // The note may have been written in either language (the language can change between sessions).
        string[] marks = ["Target libraries checked", "Цільові бібліотеки"]; // i18n-ignore: prefixes of notes already stored
        if (notes.Any(n => marks.Any(m => n.Message.StartsWith(m, StringComparison.Ordinal))))
            return;
        try
        {
            var report = await TargetCheck.RunAsync(ctx.Pool.Primary.Client, ctx.Profile, Now, ct).ConfigureAwait(false);
            foreach (var f in report.Findings.Where(f => f.Severity != "info"))
                _store.AddNote(ctx.Run.Id, "warning", L.T($"\"{f.LibraryName}\": {f.Message}"), Now);
            _store.AddNote(ctx.Run.Id, "info", report.Ok
                ? L.T("Target libraries checked: columns, content types and settings suit the move.")
                : L.T($"Target libraries checked: {report.Warnings} warnings (above)."), Now);
        }
        catch (SpoException ex) when (ex is not SpoBlockedException)
        {
            _logger.LogWarning("Run {Run}: target libraries were not checked: {Message}", ctx.Run.Id, ex.Message);
        }
    }

    private async Task<string> ApplyAsync(RunContext ctx, CancellationToken stop, CancellationToken abort)
    {
        long runId = ctx.Run.Id;
        int recovered = _store.RecoverBatches(runId);
        if (recovered > 0)
            _logger.LogInformation("Run {Run}: {Count} batches from interrupted processing will be checked before the retry", runId, recovered);
        Metrics.SetFoundBefore(_store.CountMoved(runId));
        var libraries = ctx.Targets.ToDictionary(t => t.Key, t => new Library(t.Value.DriveId!, Uri.UnescapeDataString(t.Value.DriveUrl!).TrimEnd('/')));
        using var any = CancellationTokenSource.CreateLinkedTokenSource(stop, abort);
        await NoteTargetCheckAsync(ctx, any.Token).ConfigureAwait(false);

        // EXE-3: target folders first, parents before children.
        try
        {
            foreach (var folder in _store.GetApplyFolders(runId).Where(f => !f.Done))
            {
                await WaitWhileHeldAsync(ctx, any.Token).ConfigureAwait(false);
                string parent = Core.Planning.PlanBuilder.Parent(folder.Path);
                string name = folder.Path[(parent.Length == 0 ? 0 : parent.Length + 1)..];
                await ctx.Pool.Primary.Client.CreateFolderAsync(libraries[folder.DestIndex].DriveId, parent, name, any.Token).ConfigureAwait(false);
                _store.SetApplyFolderDone(runId, folder.Ord);
            }
        }
        catch (OperationCanceledException) when (any.IsCancellationRequested)
        {
            _store.SetRunStatus(runId, RunStatuses.Stopped, Now, abort.IsCancellationRequested ? L.T("Interrupted") : L.T("Stopped"));
            return RunStatuses.Stopped;
        }
        catch (SpoException ex)
        {
            return Abort(runId, L.T($"Creating folders in the target: {ex.Message}"));
        }

        int parallel = Math.Max(1, ctx.Profile.Plan.Parallel);
        var inFlight = new Dictionary<int, Task>();
        var nextMetrics = Now + _options.MetricsInterval;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(abort);
        while (true)
        {
            if (abort.IsCancellationRequested)
            {
                await DrainAsync(inFlight).ConfigureAwait(false);
                _store.SetRunStatus(runId, RunStatuses.Stopped, Now, L.T("Interrupted"));
                return RunStatuses.Stopped;
            }
            if (ctx.Pool.AllBlocked)
                _abortReason ??= new SpoBlockedException(ctx.Pool.Primary.Name, TimeSpan.FromMinutes(ctx.Profile.Retry.BlockedMinutes)).Message;
            if (_abortReason is not null || stop.IsCancellationRequested)
            {
                // Batches in flight finish (a move cannot be taken back half way); nothing new starts.
                await DrainAsync(inFlight).ConfigureAwait(false);
                if (_abortReason is not null)
                    return Abort(runId, _abortReason);
                _store.SetRunStatus(runId, RunStatuses.Stopped, Now, L.T("Stopped"));
                return RunStatuses.Stopped;
            }
            if (Now >= nextMetrics)
            {
                _store.AddMetrics(runId, JsonSerializer.Serialize(Metrics.Snapshot(), Profile.JsonOptions), Now);
                nextMetrics = Now + _options.MetricsInterval;
            }

            bool held = _paused || ctx.Profile.Performance.BudgetAt(Now) is null || ctx.Pool.AllDailyExhausted;
            if (!held && inFlight.Count < parallel)
            {
                if (ctx.Profile.Performance.BudgetAt(Now) is { } budget)
                    ctx.Pool.SetBudgetScale(budget / 100.0);
                foreach (var batch in _store.DueBatches(runId, Now, parallel + inFlight.Count).Where(b => !inFlight.ContainsKey(b.Seq)).Take(parallel - inFlight.Count))
                {
                    _store.StartBatch(runId, batch.Seq, Now);
                    inFlight[batch.Seq] = Task.Run(() => RunBatchAsync(ctx, libraries, batch, work.Token), CancellationToken.None);
                }
            }
            if (inFlight.Count == 0 && _store.CountOpenBatches(runId) == 0)
                return Complete(runId);

            var delay = Task.Delay(_options.PollInterval, _time, any.Token);
            await Task.WhenAny(inFlight.Values.Append(delay)).ConfigureAwait(false);
            foreach (var (seq, task) in inFlight.Where(x => x.Value.IsCompleted).ToList())
            {
                inFlight.Remove(seq);
                if (task.IsFaulted)
                    _logger.LogError(task.Exception, "Batch {Seq}: processing crashed", seq);
            }
        }
    }

    private static async Task DrainAsync(Dictionary<int, Task> inFlight)
    {
        try
        {
            await Task.WhenAll(inFlight.Values).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Each batch records its own outcome.
        }
        inFlight.Clear();
    }

    /// <summary>One batch to an outcome; nothing escapes, every failure is written to the batch.</summary>
    private async Task RunBatchAsync(RunContext ctx, Dictionary<int, Library> libraries, ApplyBatch batch, CancellationToken ct)
    {
        long runId = ctx.Run.Id;
        var job = new BatchJob(this, ctx, libraries[0], libraries[batch.DestIndex], batch, ct);
        try
        {
            if (batch.IsFolder)
                await job.MoveFolderBatchAsync().ConfigureAwait(false);
            else
                await job.MoveFilesBatchAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _authFailures, 0);
            Decide(ctx, batch);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Interrupted: the next attempt starts by looking at source and target.
            _store.FinishBatch(runId, batch.Seq, BatchStatuses.Pending, Now, "job", L.T("Interrupted during the move; will be checked before the retry."));
        }
        catch (SpoBlockedException ex)
        {
            _abortReason ??= ex.Message;
            _store.FinishBatch(runId, batch.Seq, BatchStatuses.Pending, Now, "transient", ex.Message);
        }
        catch (SpoException ex) when (ex.ErrorClass == ErrorClass.Auth)
        {
            Metrics.AddError("auth");
            if (Interlocked.Increment(ref _authFailures) >= ctx.Profile.Retry.AuthFailuresToStop)
                _abortReason ??= L.T($"Mass access denials ({ex.Message}). Check the app's permissions on the site.");
            Retry(ctx, batch, "auth", ex.Message);
        }
        catch (SpoException ex) when (ex.ErrorClass == ErrorClass.Transient)
        {
            Metrics.AddError("transient");
            Retry(ctx, batch, ex is SpoUncertainException ? "job" : "transient", ex.Message);
        }
        catch (Exception ex)
        {
            Metrics.AddError("internal");
            _logger.LogError(ex, "Batch {Seq}: internal error", batch.Seq);
            _store.SetBatchFilesStatus(runId, batch.Seq, FileStatuses.Failed("internal"), Now);
            _store.FinishBatch(runId, batch.Seq, BatchStatuses.Failed("internal"), Now, "internal", ex.Message);
        }
        Metrics.SetFound(_store.CountMoved(runId));
    }

    /// <summary>RTY-2: the batch is tried again after a pause, up to <see cref="RetryOptions.FileAttempts"/> times.</summary>
    private void Retry(RunContext ctx, ApplyBatch batch, string errorClass, string message)
    {
        var current = _store.GetApplyBatch(ctx.Run.Id, batch.Seq)!;
        if (current.Attempts >= ctx.Profile.Retry.FileAttempts)
        {
            _store.ReplaceBatchFilesStatus(ctx.Run.Id, batch.Seq, FileStatuses.Pending, FileStatuses.Failed(errorClass), message, Now);
            _store.FinishBatch(ctx.Run.Id, batch.Seq, BatchStatuses.Failed(errorClass), Now, errorClass, message);
            return;
        }
        _store.FinishBatch(ctx.Run.Id, batch.Seq, BatchStatuses.Pending, Now, errorClass, message, Now + Pause(ctx, current.Attempts));
    }

    private static TimeSpan Pause(RunContext ctx, int attempt)
    {
        var pauses = ctx.Profile.Retry.FilePausesMinutes;
        return pauses.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(pauses[Math.Min(attempt, pauses.Count) - 1]);
    }

    /// <summary>
    /// The outcome of a batch from its files: all moved — done; files left in the source after a temporary error —
    /// tried again; locked files — deferred and tried in waves until the lock limit (LCK-2); otherwise partial.
    /// </summary>
    private void Decide(RunContext ctx, ApplyBatch batch)
    {
        long runId = ctx.Run.Id;
        var current = _store.GetApplyBatch(runId, batch.Seq)!;
        var counts = _store.CountBatchFiles(runId, batch.Seq);
        long Count(string s) => counts.GetValueOrDefault(s);
        if (Count(FileStatuses.Pending) > 0)
        {
            Retry(ctx, current, "transient", L.T($"{Count(FileStatuses.Pending)} files not moved because of temporary errors."));
            return;
        }
        if (Count(FileStatuses.Deferred) > 0)
        {
            var since = current.LockedSince ?? Now;
            if (Now - since < TimeSpan.FromMinutes(ctx.Profile.Retry.LockedLimitMinutes))
            {
                _store.FinishBatch(runId, batch.Seq, BatchStatuses.Deferred, Now, "locked",
                    L.T($"Locked or checked out: {Count(FileStatuses.Deferred)} files; retry with a wave."), Now + TimeSpan.FromMinutes(ctx.Profile.Retry.LockedWaveMinutes), since);
                return;
            }
            _store.ReplaceBatchFilesStatus(runId, batch.Seq, FileStatuses.Deferred, FileStatuses.Failed("locked"), L.T("The lock did not go away within the allotted time."), Now);
            counts = _store.CountBatchFiles(runId, batch.Seq);
        }
        long failed = counts.Where(c => c.Key.StartsWith("failed:", StringComparison.Ordinal)).Sum(c => c.Value);
        if (failed > 0)
        {
            string classes = string.Join(", ", counts.Where(c => c.Key.StartsWith("failed:", StringComparison.Ordinal)).Select(c => $"{c.Key[7..]} {c.Value}"));
            _store.FinishBatch(runId, batch.Seq, BatchStatuses.Partial, Now, null, L.T($"{failed} files not moved ({classes})."));
        }
        else
        {
            _store.FinishBatch(runId, batch.Seq, BatchStatuses.Done, Now);
        }
    }

    /// <summary>The work on one batch.</summary>
    private sealed class BatchJob(RunEngine engine, RunContext ctx, Library source, Library target, ApplyBatch batch, CancellationToken ct)
    {
        private Spo.Graph.GraphClient Client => ctx.Pool.Primary.Client;
        private string Site => ctx.Profile.Source!.SiteUrl!.TrimEnd('/');
        private bool Bypass => ctx.Profile.Move.BypassSharedLocks;
        private long RunId => ctx.Run.Id;
        private DateTimeOffset Now => engine.Now;
        private StateStore Store => engine._store;

        private static string Url(Library library, string path) => library.BaseUrl + "/" + path;

        private TimeSpan FolderTimeout(long items) =>
            TimeSpan.FromSeconds(ctx.Profile.Performance.RequestTimeoutSeconds + items * ctx.Profile.Move.SecondsPerItem);

        /// <summary>EXE-1, EXE-5: the whole folder in one request; what is left afterwards item by item.</summary>
        public async Task MoveFolderBatchAsync()
        {
            bool tried = batch.Attempts > 0;
            if (tried && await ReconcileFolderAsync(batch.Source, batch.Target).ConfigureAwait(false))
                return;
            try
            {
                await engine.WaitWhileHeldAsync(ctx, ct).ConfigureAwait(false);
                await Client.MoveFolderAsync(Site, Url(source, batch.Source), Url(target, batch.Target), Bypass, FolderTimeout(batch.Items), ct).ConfigureAwait(false);
            }
            catch (SpoUncertainException ex)
            {
                engine._logger.LogWarning("Batch {Seq}: the move result is unknown ({Message}); checking", batch.Seq, ex.Message);
                if (await ReconcileFolderAsync(batch.Source, batch.Target).ConfigureAwait(false))
                    return;
                return; // the source is there and the target is not: tried again later by the batch retry
            }
            catch (SpoException ex) when (ex.IsThreshold || ex.IsDestinationExists || ex.ErrorClass == ErrorClass.Locked)
            {
                engine._logger.LogInformation("Batch {Seq}: the folder was not moved as a whole ({Message}); item by item", batch.Seq, ex.Message);
                await MoveContentsAsync(batch.Source, batch.Target).ConfigureAwait(false);
                await SettleLeftoversAsync().ConfigureAwait(false);
                return;
            }
            catch (SpoException ex) when (ex.IsNotFound)
            {
                await ReconcileFolderAsync(batch.Source, batch.Target).ConfigureAwait(false);
                return;
            }
            // EXE-5: SharePoint answered OK; the source must be gone.
            if (await Client.FindItemByPathAsync(source.DriveId, batch.Source, ct).ConfigureAwait(false) is null)
            {
                Store.SetBatchFilesStatus(RunId, batch.Seq, FileStatuses.Moved, Now);
                return;
            }
            await MoveContentsAsync(batch.Source, batch.Target).ConfigureAwait(false);
            await SettleLeftoversAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// EXE-8: after an unknown outcome. Source gone and target there — moved. Source gone and no target — the files
        /// left the source before the move. Both there — the rest goes item by item. True when the batch is settled.
        /// </summary>
        private async Task<bool> ReconcileFolderAsync(string src, string dst)
        {
            var s = await Client.FindItemByPathAsync(source.DriveId, src, ct).ConfigureAwait(false);
            var t = await Client.FindItemByPathAsync(target.DriveId, dst, ct).ConfigureAwait(false);
            if (s is null)
            {
                Store.SetBatchFilesStatus(RunId, batch.Seq, t is null ? FileStatuses.SkippedNotFound : FileStatuses.Moved, Now);
                return true;
            }
            if (t is null)
                return false;
            await MoveContentsAsync(src, dst).ConfigureAwait(false);
            await SettleLeftoversAsync().ConfigureAwait(false);
            return true;
        }

        /// <summary>EXE-5: the children of a source folder into a target folder: files one by one, folders whole where possible.</summary>
        private async Task MoveContentsAsync(string src, string dst)
        {
            string parent = Core.Planning.PlanBuilder.Parent(dst);
            await Client.CreateFolderAsync(target.DriveId, parent, dst[(parent.Length == 0 ? 0 : parent.Length + 1)..], ct).ConfigureAwait(false);
            var children = await Client.GetChildrenAsync(source.DriveId, src, ct).ConfigureAwait(false);
            foreach (var child in children.Where(c => !c.IsFolder).OrderBy(c => c.Name, StringComparer.Ordinal))
                await MoveFileAsync(src + "/" + child.Name, dst + "/" + child.Name).ConfigureAwait(false);
            foreach (var child in children.Where(c => c.IsFolder).OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                string from = src + "/" + child.Name, to = dst + "/" + child.Name;
                if (await Client.FindItemByPathAsync(target.DriveId, to, ct).ConfigureAwait(false) is null)
                {
                    try
                    {
                        await engine.WaitWhileHeldAsync(ctx, ct).ConfigureAwait(false);
                        await Client.MoveFolderAsync(Site, Url(source, from), Url(target, to), Bypass, FolderTimeout(batch.Items), ct).ConfigureAwait(false);
                        if (await Client.FindItemByPathAsync(source.DriveId, from, ct).ConfigureAwait(false) is null)
                        {
                            Store.SetBatchFilesStatusUnder(RunId, batch.Seq, from, FileStatuses.Moved, Now);
                            continue;
                        }
                    }
                    catch (SpoException ex) when (ex is SpoUncertainException || ex.IsThreshold || ex.IsDestinationExists || ex.ErrorClass == ErrorClass.Locked)
                    {
                        // Item by item below.
                    }
                }
                await MoveContentsAsync(from, to).ConfigureAwait(false);
            }
        }

        /// <summary>Files batch: every file not yet final, one request each.</summary>
        public async Task MoveFilesBatchAsync()
        {
            foreach (var f in Store.GetBatchFiles(RunId, batch.Seq).Where(f => !FileStatuses.IsFinal(f.Status)))
                await MoveFileAsync(f.Path, f.NewPath).ConfigureAwait(false);
        }

        /// <summary>One file; its outcome goes to its row (a file the inventory did not see is moved without one).</summary>
        private async Task MoveFileAsync(string src, string dst)
        {
            ct.ThrowIfCancellationRequested();
            await engine.WaitWhileHeldAsync(ctx, ct).ConfigureAwait(false);
            string status;
            string? errorClass = null, error = null;
            try
            {
                await Client.MoveFileAsync(Site, Url(source, src), Url(target, dst), Bypass, ct).ConfigureAwait(false);
                status = FileStatuses.Moved;
            }
            catch (SpoUncertainException ex)
            {
                bool there = await Client.FindItemByPathAsync(target.DriveId, dst, ct).ConfigureAwait(false) is not null;
                bool gone = await Client.FindItemByPathAsync(source.DriveId, src, ct).ConfigureAwait(false) is null;
                (status, errorClass, error) = gone && there ? (FileStatuses.Moved, null, null) : (FileStatuses.Pending, "job", ex.Message);
            }
            catch (SpoException ex) when (ex.IsNotFound)
            {
                bool there = await Client.FindItemByPathAsync(target.DriveId, dst, ct).ConfigureAwait(false) is not null;
                (status, errorClass, error) = there ? (FileStatuses.Moved, null, null) : (FileStatuses.SkippedNotFound, "notfound", ex.Message);
            }
            catch (SpoException ex) when (ex.IsDestinationExists || ex.ErrorClass == ErrorClass.Conflict)
            {
                (status, errorClass, error) = (FileStatuses.Failed("conflict"), "conflict", L.T("The target already has a file with this name; not overwritten."));
            }
            catch (SpoException ex) when (ex.ErrorClass == ErrorClass.Locked)
            {
                (status, errorClass, error) = (FileStatuses.Deferred, "locked", ex.Message);
            }
            catch (SpoException ex) when (ex.ErrorClass == ErrorClass.Transient)
            {
                engine.Metrics.AddError("transient");
                (status, errorClass, error) = (FileStatuses.Pending, "transient", ex.Message);
            }
            catch (SpoException ex) when (ex.ErrorClass is ErrorClass.File or ErrorClass.Internal)
            {
                (status, errorClass, error) = (FileStatuses.Failed("file"), "file", ex.Message);
            }
            if (errorClass is not null && errorClass != "notfound")
                engine.Metrics.AddError(errorClass);
            Store.SetBatchFileOutcome(RunId, batch.Seq, src, status, errorClass, error, Now);
        }

        /// <summary>
        /// Files of the batch still pending after the source was gone through: not in the source any more, so either
        /// moved earlier (by the folder request that did not finish) or gone; one look at the target decides.
        /// </summary>
        private async Task SettleLeftoversAsync()
        {
            foreach (var f in Store.GetBatchFiles(RunId, batch.Seq).Where(f => f.Status == FileStatuses.Pending))
            {
                if (await Client.FindItemByPathAsync(source.DriveId, f.Path, ct).ConfigureAwait(false) is not null)
                    continue; // still there: a temporary error, the batch is tried again
                bool there = await Client.FindItemByPathAsync(target.DriveId, f.NewPath, ct).ConfigureAwait(false) is not null;
                Store.SetBatchFileOutcome(RunId, batch.Seq, f.Path, there ? FileStatuses.Moved : FileStatuses.SkippedNotFound, there ? null : "notfound", null, Now);
            }
        }
    }
}
