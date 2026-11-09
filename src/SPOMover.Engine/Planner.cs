using System.Globalization;
using System.Text;
using System.Text.Json;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Core.Mapping;
using SPOMover.Core.Planning;
using SPOMover.Core.Reports;
using SPOMover.State;

namespace SPOMover.Engine;

/// <summary>PLN-3: a wave with its forecast.</summary>
public sealed record WaveForecast(int Number, int RuleRow, string Title, int Batches, long Files, long Bytes, TimeSpan Duration);

/// <summary>PLN-4: one batch that differs between two plans. Change: added, removed or changed (other files or size).</summary>
public sealed record BatchChange(string Change, BatchKind Kind, string Source, string Library, string Target, long FilesBefore, long FilesAfter, long BytesBefore, long BytesAfter);

/// <summary>PLN-4: what changed since the previous plan of the profile (after a new inventory or another mapping).</summary>
public sealed record PlanDiff(long PreviousPlanId, long PreviousRunId, int Added, int Removed, int Changed, long FilesBefore, long FilesAfter, IReadOnlyList<BatchChange> Changes)
{
    public bool Same => Added == 0 && Removed == 0 && Changed == 0;
}

/// <summary>A saved plan with its waves, forecast and the problems found while building it.</summary>
public sealed record PlanReport(
    StateStore.PlanRow Plan,
    IReadOnlyList<WaveForecast> Waves,
    TimeSpan Duration,
    long FolderBatches,
    long FilesBatches,
    long CheckedOut,
    long LargestBatchItems,
    IReadOnlyList<ValidationIssue> Issues,
    PlanDiff? Diff = null)
{
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
}

/// <summary>
/// PLN-1…PLN-5: builds the plan of a profile from its last inventory (<see cref="PlanBuilder"/>), keeps it as a
/// snapshot, and gives the reports for approval. Nothing is moved here.
/// </summary>
public static class Planner
{
    public const string Area = "plan";

    public static readonly string[] Reports = ["batches", "waves", "folders", "conflicts", "files", "changes"];

    public static PlanReport Build(StateStore store, Profile profile, DateTimeOffset now, RunRow? inventory = null)
    {
        inventory ??= MappingCheck.LastInventory(store, profile)
            ?? throw new InvalidOperationException(L.T("This profile has no finished inventory of the source yet. Run inventory first."));
        var build = profile.BuildMapping();
        var errors = build.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0 || build.Set is null)
            throw new InvalidOperationException(L.T("The plan is not built while the mapping has errors:") + "\n" + string.Join("\n", errors));
        var limitIssues = profile.Plan.Validate().Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (limitIssues.Count > 0)
            throw new InvalidOperationException(string.Join("\n", limitIssues));

        var issues = new List<ValidationIssue>();
        var runLibraries = RunConfig.Parse(inventory.ConfigSnapshot).Profile.Libraries();
        var targetFolders = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        var targetFiles = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in profile.TargetLibraries.Select(l => l.Trim()))
        {
            int index = Enumerable.Range(1, Math.Max(0, runLibraries.Count - 1))
                .FirstOrDefault(k => string.Equals(runLibraries[k].Library?.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (index == 0)
            {
                issues.Add(new(IssueSeverity.Warning, L.T($"Library \"{name}\" was not listed in run {inventory.Id}: its existing folders and files were not taken into account, conflicts will show up during the move. Repeat the inventory."), null, Area));
                continue;
            }
            targetFolders[name] = store.GetFolderStats(inventory.Id, index).Select(s => s.Path).Where(p => p.Length > 0).ToList();
            targetFiles[name] = store.FilePaths(inventory.Id, index).Select(f => f.Path).ToList();
        }

        var folders = store.GetFolderStats(inventory.Id, 0).Select(s => new PlanFolder(s.Path, s.Files, s.Bytes)).ToList();
        var result = PlanBuilder.Build(build.Set, folders, store.PlanSourceFiles(inventory.Id), targetFolders, targetFiles, new PlanLimits(profile.Plan.MaxBatchItems));

        string mapping = JsonSerializer.Serialize(profile.Mapping, Profile.JsonOptions);
        string options = JsonSerializer.Serialize(profile.Plan, Profile.JsonOptions);
        long id = store.SavePlan(inventory.Id, profile.Name, mapping, options, result, now);

        if (result.Conflicts.Count > 0)
            issues.Add(new(IssueSeverity.Warning, L.T($"Not planned because the path in the target is taken: {Ui(result.Conflicts.Count)} files (\"Conflicts\" report). They stay in the source until decided."), null, Area));
        long checkedOut = result.Batches.Sum(b => (long)b.CheckedOut);
        if (checkedOut > 0)
            issues.Add(new(IssueSeverity.Warning, L.T($"The batches have {Ui(checkedOut)} checked-out files: they must be released before the wave (LCK-1), otherwise the batch moves only in part."), null, Area));
        if (store.GetTargets(inventory.Id).FirstOrDefault(t => t.Index == 0) is { Checkouts: CheckoutScan.Skipped })
            issues.Add(new(IssueSeverity.Warning, L.T("Files without a published version were not checked in this inventory: if the folders of the batches have any, those batches will move only in part."), null, Area));
        if (result.Batches.Count == 0)
            issues.Add(new(IssueSeverity.Warning, L.T("The plan is empty: the mapping moves nothing."), null, Area));

        return Report(store, id, issues);
    }

    /// <summary>The newest plan of the profile, if any.</summary>
    public static StateStore.PlanRow? Latest(StateStore store, Profile profile) =>
        profile.Name is null ? null : store.ListPlans(profile.Name, 1).FirstOrDefault();

    public static PlanReport Report(StateStore store, long planId, IReadOnlyList<ValidationIssue>? issues = null)
    {
        var plan = store.GetPlan(planId) ?? throw new ArgumentException(L.T($"Plan {planId} not found."));
        var options = JsonSerializer.Deserialize<PlanOptions>(plan.OptionsJson, Profile.JsonOptions) ?? new PlanOptions();
        var batches = store.GetPlanBatches(planId);
        var waves = store.GetPlanWaves(planId).Select(w => new WaveForecast(w.Number, w.RuleRow, w.Title, w.Batches, w.Files, w.Bytes, Forecast(w.Files, options))).ToList();
        return new PlanReport(plan, waves, Forecast(plan.Files, options),
            batches.Count(b => b.Kind == BatchKind.Folder), batches.Count(b => b.Kind == BatchKind.Files),
            batches.Sum(b => (long)b.CheckedOut), batches.Count == 0 ? 0 : batches.Max(b => b.Items), issues ?? [], Diff(store, planId));
    }

    /// <summary>
    /// PLN-4: the plan compared with the previous plan of the same profile. Batches are matched by what they move and
    /// where (kind, source folder, library, target folder); a files batch split into several counts as one.
    /// </summary>
    public static PlanDiff? Diff(StateStore store, long planId)
    {
        var plan = store.GetPlan(planId);
        if (plan?.ProfileName is null)
            return null;
        var previous = store.ListPlans(plan.ProfileName, 500).FirstOrDefault(p => p.Id < planId);
        if (previous is null)
            return null;
        static Dictionary<(BatchKind, string, string, string), (BatchKind Kind, string Source, string Library, string Target, long Files, long Bytes)> Group(IEnumerable<PlanBatch> batches) =>
            batches.GroupBy(b => (b.Kind, Core.Mapping.MappingSet.Key(b.Source), b.Library.ToLowerInvariant(), Core.Mapping.MappingSet.Key(b.TargetPath)))
                .ToDictionary(g => g.Key, g => (g.First().Kind, g.First().Source, g.First().Library, g.First().TargetPath, g.Sum(b => b.Files), g.Sum(b => b.Bytes)));
        var before = Group(store.GetPlanBatches(previous.Id));
        var after = Group(store.GetPlanBatches(planId));
        var changes = new List<BatchChange>();
        foreach (var (key, a) in after)
        {
            if (!before.TryGetValue(key, out var b))
                changes.Add(new BatchChange("added", a.Kind, a.Source, a.Library, a.Target, 0, a.Files, 0, a.Bytes));
            else if (b.Files != a.Files || b.Bytes != a.Bytes)
                changes.Add(new BatchChange("changed", a.Kind, a.Source, a.Library, a.Target, b.Files, a.Files, b.Bytes, a.Bytes));
        }
        foreach (var (key, b) in before.Where(x => !after.ContainsKey(x.Key)))
            changes.Add(new BatchChange("removed", b.Kind, b.Source, b.Library, b.Target, b.Files, 0, b.Bytes, 0));
        changes = changes.OrderBy(c => Core.Mapping.MappingSet.Key(c.Source), StringComparer.Ordinal).ThenBy(c => c.Change, StringComparer.Ordinal).ToList();
        return new PlanDiff(previous.Id, previous.RunId, changes.Count(c => c.Change == "added"), changes.Count(c => c.Change == "removed"),
            changes.Count(c => c.Change == "changed"), previous.Files, plan.Files, changes);
    }

    /// <summary>PLN-3: pure moving time at the measured speed with <see cref="PlanOptions.Parallel"/> batches at once.</summary>
    public static TimeSpan Forecast(long files, PlanOptions options) =>
        TimeSpan.FromSeconds(files / (options.FilesPerSecond * options.Parallel));

    // ------------------------------------------------------------------ reports (PLN-5)

    public static string FileName(string profile, long planId, string name, string format) => $"{profile}-plan{planId}-{name}.{format}";

    public static IEnumerable<IReadOnlyList<string?>> Rows(StateStore store, long planId, string name)
    {
        _ = store.GetPlan(planId) ?? throw new ArgumentException(L.T($"Plan {planId} not found."));
        switch (name)
        {
            case "batches":
                return Prepend(["seq", "wave", "kind", "rule", "source", "library", "target", "files", "bytes", "items", "checked_out"],
                    store.GetPlanBatches(planId).Select(b => (IReadOnlyList<string?>)[N(b.Seq), N(b.Wave), b.Kind == BatchKind.Folder ? "folder" : "files", N(b.RuleRow),
                        Slash(b.Source), b.Library, Slash(b.TargetPath), N(b.Files), N(b.Bytes), N(b.Items), N(b.CheckedOut)]));
            case "waves":
                var report = Report(store, planId);
                return Prepend(["wave", "rule", "title", "batches", "files", "bytes", "forecast_hours"],
                    report.Waves.Select(w => (IReadOnlyList<string?>)[N(w.Number), N(w.RuleRow), w.Title, N(w.Batches), N(w.Files), N(w.Bytes),
                        w.Duration.TotalHours.ToString("0.00", CultureInfo.InvariantCulture)]));
            case "folders":
                return Prepend(["library", "path"], store.GetPlanFolders(planId).Select(f => (IReadOnlyList<string?>)[f.Library, f.Path]));
            case "conflicts":
                return Prepend(["source_path", "library", "new_path", "reason"],
                    store.GetPlanConflicts(planId).Select(c => (IReadOnlyList<string?>)[c.Path, c.Library, c.NewPath, c.Reason]));
            case "changes":
                var diff = Diff(store, planId);
                return Prepend(["change", "kind", "source", "library", "target", "files_before", "files_after", "bytes_before", "bytes_after"],
                    (diff?.Changes ?? []).Select(c => (IReadOnlyList<string?>)[c.Change, c.Kind == BatchKind.Folder ? "folder" : "files", Slash(c.Source), c.Library,
                        Slash(c.Target), N(c.FilesBefore), N(c.FilesAfter), N(c.BytesBefore), N(c.BytesAfter)]));
            case "files":
                return Prepend(["seq", "source_path", "new_path", "size"],
                    store.GetPlanBatchFiles(planId).Select(x => (IReadOnlyList<string?>)[N(x.Seq), x.File.Path, x.File.NewPath, N(x.File.Size)]));
            default:
                throw new ArgumentException(L.T($"Unknown report \"{name}\"."), nameof(name));
        }
    }

    public static void WriteCsv(StateStore store, long planId, string name, Stream output) =>
        ReportExporter.WriteCsv(output, Rows(store, planId, name));

    public static void WriteXlsx(StateStore store, long planId, string name, Stream output) =>
        SimpleXlsxWriter.Write(output, name, Rows(store, planId, name));

    /// <summary>A short text summary for the CLI.</summary>
    public static string Summary(PlanReport r)
    {
        var p = r.Plan;
        var sb = new StringBuilder();
        sb.AppendLine(L.T($"Plan {p.Id} from inventory {p.RunId}, {p.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm}"));
        sb.AppendLine(L.T($"Batches: {Ui(p.Batches)} (whole folders {Ui(r.FolderBatches)}, sets of files {Ui(r.FilesBatches)}); the largest — {Ui(r.LargestBatchItems)} items"));
        sb.AppendLine(L.T($"Moving: {Ui(p.Files)} files, {Gb(p.Bytes)}; staying: {Ui(p.StayFiles)} files, {Gb(p.StayBytes)}"));
        sb.AppendLine(L.T($"Folders to create in the target: {Ui(p.Folders)}; conflicts: {Ui(p.Conflicts)}; checked out in the batches: {Ui(r.CheckedOut)}"));
        sb.AppendLine(L.T($"Forecast: {Hours(r.Duration)} of net time"));
        if (r.Diff is { } d)
        {
            sb.AppendLine(d.Same
                ? L.T($"Compared with plan {d.PreviousPlanId} (inventory {d.PreviousRunId}): the batches are the same.")
                : L.T($"Compared with plan {d.PreviousPlanId} (inventory {d.PreviousRunId}): batches added {Ui(d.Added)}, removed {Ui(d.Removed)}, changed {Ui(d.Changed)}; files {Ui(d.FilesBefore)} → {Ui(d.FilesAfter)} (\"Changes\" report)."));
        }
        sb.AppendLine(L.T("Waves:"));
        foreach (var w in r.Waves)
            sb.AppendLine(L.T($"  {w.Number,3}. {w.Title,-50} {w.Batches,6} batches {Ui(w.Files),12} files {Gb(w.Bytes),12} {Hours(w.Duration),10}"));
        foreach (var i in r.Issues)
            sb.AppendLine(i.ToString());
        return sb.ToString();
    }

    private static string Hours(TimeSpan t) => t.TotalHours >= 1 ? L.T($"{t.TotalHours:0.0} h") : L.T($"{Math.Ceiling(t.TotalMinutes):0} min");

    private static string Ui(long n) => n.ToString("N0", L.Culture);

    private static string Gb(long bytes)
    {
        string value = (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture);
        return L.T($"{value} GB");
    }

    private static string Slash(string path) => path.Length == 0 ? "/" : path;

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<IReadOnlyList<string?>> Prepend(string?[] header, IEnumerable<IReadOnlyList<string?>> rows) => rows.Prepend(header);
}
