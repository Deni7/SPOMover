using System.Globalization;
using System.Text;
using System.Text.Json;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.State;

namespace SPOMover.Engine;

/// <summary>
/// Stage 6: what an apply run measured — speed per batch and in total, load on SharePoint, locks and conflicts,
/// verify — and the forecast for the rest of the source from those numbers, with the setting to put into the profile
/// (<c>plan.filesPerSecond</c>). Adapted from the pilot report of SPOReplaceLink.
/// </summary>
public static class PilotReport
{
    /// <summary>RU, requests and 429/503 over all sessions of a run (each start counts from zero, so a session's total is its largest value).</summary>
    public sealed record LoadTotals(long Ru, long Requests, long Throttled429, long Throttled503, IReadOnlyDictionary<string, long> RuByApp);

    public static LoadTotals Load(IEnumerable<string> snapshots)
    {
        var sessions = new Dictionary<DateTimeOffset, Dictionary<string, long>>();
        foreach (string json in snapshots)
        {
            MetricsSnapshot? s;
            try
            {
                s = JsonSerializer.Deserialize<MetricsSnapshot>(json, Profile.JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (s is null)
                continue;
            var current = sessions.TryGetValue(s.Started ?? DateTimeOffset.MinValue, out var d) ? d : sessions[s.Started ?? DateTimeOffset.MinValue] = new(StringComparer.Ordinal);
            void Max(string key, long value) => current[key] = Math.Max(current.GetValueOrDefault(key), value);
            foreach (var a in s.Apps)
            {
                Max("ru:" + a.Name, a.RuSpent);
                Max("req:" + a.Name, a.Requests);
                Max("429:" + a.Name, a.Throttled429);
                Max("503:" + a.Name, a.Throttled503);
            }
        }
        long Sum(string prefix) => sessions.Values.SelectMany(d => d).Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(x => x.Value);
        var byApp = sessions.Values.SelectMany(d => d).Where(x => x.Key.StartsWith("ru:", StringComparison.Ordinal))
            .GroupBy(x => x.Key[3..]).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
        return new LoadTotals(Sum("ru:"), Sum("req:"), Sum("429:"), Sum("503:"), byApp);
    }

    /// <summary>Time some batch was being moved: the union of the batches' start–finish intervals (pauses and stops left out).</summary>
    public static TimeSpan ActiveTime(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var total = TimeSpan.Zero;
        DateTimeOffset? from = null, to = null;
        foreach (var (start, end) in intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start))
        {
            if (to is null || start > to)
            {
                if (from is not null)
                    total += to!.Value - from.Value;
                (from, to) = (start, end);
            }
            else if (end > to)
            {
                to = end;
            }
        }
        if (from is not null)
            total += to!.Value - from.Value;
        return total;
    }

    /// <summary>Median of the per-batch speeds (files per second), weighted by nothing: a typical batch.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public static string Text(StateStore store, long runId)
    {
        var run = store.GetRun(runId) ?? throw new KeyNotFoundException(L.T($"Run {runId} not found."));
        if (run.Mode != RunModes.Apply)
            throw new InvalidOperationException(L.T($"Run {runId} is not a move (apply): the pilot report is built for a move."));
        var profile = RunConfig.Parse(run.ConfigSnapshot).Profile;
        var counts = store.CountByStatus(runId);
        long Count(string status) => counts.Where(c => c.Key == status || c.Key.StartsWith(status + ":", StringComparison.Ordinal)).Sum(c => c.Value);
        long verified = Count(FileStatuses.Verified), moved = Count(FileStatuses.Moved) + verified;
        long planned = counts.Values.Sum();
        var (_, bytes) = store.Totals(runId);
        var batches = store.GetApplyBatches(runId);
        var load = Load(store.GetMetrics(runId).Select(m => m.Json));
        var attempts = store.CountAttemptsByClass(runId);
        var timed = batches.Where(b => b.StartedAt is not null && b.FinishedAt is not null && b.FinishedAt > b.StartedAt).ToList();
        var active = ActiveTime(timed.Select(b => (b.StartedAt!.Value, b.FinishedAt!.Value)));
        var perBatch = timed.Where(b => b.Files >= 20).Select(b => b.Files / (b.FinishedAt!.Value - b.StartedAt!.Value).TotalSeconds).ToList();
        double median = Median(perBatch);
        double overall = active > TimeSpan.Zero ? moved / active.TotalSeconds : 0;
        int parallel = Math.Max(1, profile.Plan.Parallel);
        var wall = (run.FinishedAt ?? DateTimeOffset.UtcNow) - (run.StartedAt ?? run.CreatedAt);

        var sb = new StringBuilder();
        void Line(string text = "") => sb.AppendLine(text);
        static string N(double v, string format = "N0") => v.ToString(format, L.Culture);
        static string N1(double v) => N(v, "N1");
        static string N2(double v) => N(v, "N2");

        string plan = store.GetRunPlan(runId)?.ToString(CultureInfo.InvariantCulture) ?? "—";
        string stop = run.StopReason is null ? string.Empty : " — " + run.StopReason;
        Line(L.T($"Pilot report: run {run.Id} (move), profile {run.ProfileName}, plan {plan}"));
        Line(L.T($"Status: {run.Status}{stop}"));
        Line(L.T($"Source: {profile.Source}"));
        Line(L.T($"From start to finish: {Duration(wall)}; moving time (at least one batch running): {Duration(active)}"));
        Line();

        Line(L.T("1. What was moved"));
        Line(L.T($"  files in the plan: {N(planned)}; moved: {N(moved)} ({N2(Gb(bytes))} GB in the plan); verified: {N(verified)}"));
        long batchesDone = batches.Count(b => b.Status == StateStore.BatchStatuses.Done);
        long batchesPartial = batches.Count(b => b.Status == StateStore.BatchStatuses.Partial);
        long batchesFailed = batches.Count(b => b.Status.StartsWith("failed:", StringComparison.Ordinal));
        long batchesOpen = batches.Count(b => !StateStore.BatchStatuses.IsFinal(b.Status));
        Line(L.T($"  batches: {N(batches.Count)} — done {N(batchesDone)}, partial {N(batchesPartial)}, failed {N(batchesFailed)}, not finished yet {N(batchesOpen)}"));
        Line(L.T($"  as a whole folder: {N(batches.Count(b => b.IsFolder))} batches, as a set of files: {N(batches.Count(b => !b.IsFolder))}"));
        foreach (var g in batches.GroupBy(b => b.Library).OrderBy(g => g.Key, StringComparer.CurrentCulture))
            Line(L.T($"  → \"{g.Key}\": {N(g.Sum(b => b.Files))} files in {N(g.Count())} batches"));
        Line();

        Line(L.T("2. Speed"));
        if (perBatch.Count == 0)
        {
            Line(L.T("  not measured: no finished batches with 20 or more files."));
        }
        else
        {
            Line(L.T($"  per batch (median of {N(perBatch.Count)} batches): {N2(median)} files/s; from {N2(perBatch.Min())} to {N2(perBatch.Max())}"));
            Line(L.T($"  overall while moving: {N2(overall)} files/s ({N(overall * 3600)} files/h) with {parallel} batches at a time"));
            string setting = median.ToString("0.0", CultureInfo.InvariantCulture);
            Line(L.T($"  the plan forecast assumed {N1(profile.Plan.FilesPerSecond)} files/s per batch; measured {N2(median)} — set plan.filesPerSecond = {setting} in the profile (\"Planning\" tab)."));
            if (overall > 0 && overall < median * parallel * 0.6)
                Line(L.T("  ! The overall speed is noticeably lower than \"batch × parallelism\": batches were waiting (locked files, retries, pause) or the waves had few batches."));
        }
        Line();

        Line(L.T("3. Load on SharePoint"));
        string ruByApp = load.RuByApp.Count > 1 ? " (" + string.Join(", ", load.RuByApp.Select(x => $"{x.Key}: {N(x.Value)}")) + ")" : string.Empty;
        Line(L.T($"  RU spent: {N(load.Ru)}{ruByApp}; requests: {N(load.Requests)}"));
        if (moved > 0)
            Line(L.T($"  RU per 1,000 moved files: {N1(load.Ru * 1000.0 / moved)}"));
        Line(L.T($"  429 responses: {N(load.Throttled429)}; 503: {N(load.Throttled503)}"));
        if (load.Throttled429 + load.Throttled503 == 0)
            Line(L.T($"  no throttling: parallelism can be raised (plan.parallel, now {parallel}), watching 429/503 on the dashboard."));
        else
            Line(L.T("  ! There were 429/503: do not raise parallelism; run the next waves outside working hours."));
        Line();

        Line(L.T("4. Locks, conflicts, errors"));
        long lockedAttempts = attempts.GetValueOrDefault("locked");
        long lockedFailed = Count("failed:locked"), deferred = Count(FileStatuses.Deferred);
        string lockedShare = Share(lockedFailed, planned);
        Line(L.T($"  attempts on a locked file (open for editing, checked out): {N(lockedAttempts)}; files deferred now: {N(deferred)}; gave up waiting: {N(lockedFailed)} ({lockedShare})"));
        long conflicts = Count("failed:conflict");
        Line(L.T($"  name conflicts in the target: {N(conflicts)}; gone from the source before the move: {N(Count(FileStatuses.SkippedNotFound))}"));
        foreach (var (code, n) in counts.Where(c => c.Key.StartsWith("failed:", StringComparison.Ordinal) && c.Key is not ("failed:locked" or "failed:conflict")).OrderByDescending(c => c.Value))
            Line($"  {code}: {N(n)}");
        if (lockedFailed > 0)
            Line(L.T("  → locked files: \"Retry failed…\" (class locked) once their owners close them; or move open files (move.bypassSharedLocks)."));
        Line();

        Line(L.T("5. Verification"));
        long verifyFailed = Count("failed:verify");
        if (verified + verifyFailed == 0)
        {
            Line(L.T("  not verified yet: \"Verify…\" on the run card (or spo-mover verify --update)."));
        }
        else
        {
            string notVerified = moved > verified ? L.T($"; not verified: {N(moved - verified)}") : string.Empty;
            Line(L.T($"  confirmed: {N(verified)}; not confirmed: {N(verifyFailed)}{notVerified}"));
        }
        Line();

        Line(L.T("6. Forecast for the rest"));
        var inventory = MappingCheck.LastInventory(store, profile);
        long remaining = 0;
        if (inventory is not null)
        {
            long inSource = store.BreakdownBy(inventory.Id, "target_idx").FirstOrDefault(b => b.Key == "0")?.Total ?? 0;
            bool before = run.FinishedAt is null || inventory.CreatedAt < run.FinishedAt;
            remaining = Math.Max(0, inSource - (before ? moved : 0));
            string beforeNote = before ? L.T(", taken before this move, moved files excluded") : string.Empty;
            Line(L.T($"  ≈ {N(remaining)} files remain in the source (inventory {inventory.Id}{beforeNote})"));
        }
        else
        {
            Line(L.T("  no inventory of the profile found: the forecast comes after one."));
        }
        double rate = median > 0 ? median : overall / parallel;
        if (remaining > 0 && rate > 0)
        {
            foreach (int p in new[] { parallel, parallel * 2 }.Distinct())
                Line(L.T($"  with {p} batches at a time and {N2(rate)} files/s per batch: ≈ {Hours(remaining / (rate * p) / 3600)} of net time"));
            string average = moved > 0 && planned > 0 ? L.T($"{N(bytes / 1024.0 / planned)} KB per file") : "—";
            Line(L.T($"  not counting waits for locked files and breaks between waves; the speed depends on file size (pilot: on average {average})."));
        }
        Line();

        Line(L.T("7. Check manually"));
        Line(L.T("  - a few moved files in the new library: they open, columns and authors are in place;"));
        Line(L.T("  - the \"Rules for SPOReplaceLink\" report — run SPOReplaceLink over the new libraries;"));
        Line(L.T("  - empty folders in the source — \"Delete empty folders…\" after verification;"));
        Line(L.T("  - the run notes (target library check) and \"Need attention\"."));
        return sb.ToString();
    }

    private static double Gb(long bytes) => bytes / 1024.0 / 1024.0 / 1024.0;

    private static string Share(long part, long whole) => whole == 0 ? "—" : (100.0 * part / whole).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? L.T($"{(int)t.TotalHours} h {t.Minutes} min")
        : L.T($"{t.Minutes} min {t.Seconds} s");

    private static string Hours(double hours)
    {
        string value = hours < 1
            ? Math.Max(1, Math.Round(hours * 60)).ToString(CultureInfo.InvariantCulture)
            : hours.ToString("0.0", CultureInfo.InvariantCulture);
        return hours < 1 ? L.T($"{value} min") : L.T($"{value} h");
    }
}
