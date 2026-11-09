using System.Globalization;
using System.Text;
using SPOMover.Core.Localization;
using SPOMover.Core.Reports;
using SPOMover.State;

namespace SPOMover.Engine;

/// <summary>verify: what a later inventory says about the files of an apply run.</summary>
public sealed record VerifyReport(
    long RunId,
    long InventoryRunId,
    long Verified,
    long Failed,
    long NotMoved,
    IReadOnlyList<(int Seq, string Path)> NewInSource)
{
    public bool Ok => Failed == 0 && NewInSource.Count == 0;
}

/// <summary>
/// After an apply run: verify (FEATURES 8 — every file of a batch is in the target, the source subtree is empty) and
/// the rules for SPOReplaceLink (MAP-8).
/// </summary>
public static class ApplyResults
{
    public static readonly string[] Reports = ["verify", "rules", "pilot"];

    /// <summary>Reports that are text (TXT), not tables.</summary>
    public static bool IsText(string name) => name == "pilot";

    /// <summary>
    /// Checks the apply run against the newest finished inventory of its profile made after the run finished (an update
    /// with changes only is enough). Moved files become <c>verified</c> or <c>failed:verify</c>.
    /// </summary>
    public static VerifyReport Verify(StateStore store, long applyRun, DateTimeOffset now)
    {
        var run = store.GetRun(applyRun) ?? throw new KeyNotFoundException(L.T($"Run {applyRun} not found."));
        if (run.Mode != RunModes.Apply)
            throw new InvalidOperationException(L.T($"Run {applyRun} is not a move (apply)."));
        if (!RunStatuses.IsFinished(run.Status) || run.FinishedAt is null)
            throw new InvalidOperationException(L.T($"Run {applyRun} has not finished yet ({run.Status})."));
        var profile = RunConfig.Parse(run.ConfigSnapshot).Profile;
        var inventory = MappingCheck.LastInventory(store, profile);
        if (inventory is null || inventory.CreatedAt < run.FinishedAt)
            throw new InvalidOperationException(L.T($"Verification needs an inventory made after the move. Run: spo-mover inventory {profile.Name} --update --wait (or verify with --update)."));

        var applyLibraries = profile.Libraries();
        var inventoryLibraries = RunConfig.Parse(inventory.ConfigSnapshot).Profile.Libraries();
        var map = new Dictionary<int, int>();
        for (int i = 1; i < applyLibraries.Count; i++)
        {
            int j = Enumerable.Range(1, Math.Max(0, inventoryLibraries.Count - 1))
                .FirstOrDefault(k => string.Equals(inventoryLibraries[k].Library?.Trim(), applyLibraries[i].Library?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (j == 0)
                throw new InvalidOperationException(L.T($"Library \"{applyLibraries[i].Library}\" was not listed in inventory {inventory.Id}."));
            map[i] = j;
        }
        store.VerifyApply(applyRun, inventory.Id, map, now);
        var counts = store.CountByStatus(applyRun);
        long verified = counts.GetValueOrDefault(FileStatuses.Verified);
        long failed = counts.GetValueOrDefault("failed:verify");
        long notMoved = counts.Where(c => c.Key != FileStatuses.Verified && c.Key != "failed:verify").Sum(c => c.Value);
        return new VerifyReport(applyRun, inventory.Id, verified, failed, notMoved, store.NewInSource(applyRun, inventory.Id));
    }

    public static string Summary(VerifyReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T($"Verification of run {r.RunId} against inventory {r.InventoryRunId}"));
        sb.AppendLine(L.T($"Confirmed: {Ui(r.Verified)}; not confirmed: {Ui(r.Failed)}; not moved (conflict, lock, etc.): {Ui(r.NotMoved)}"));
        string newNote = r.NewInSource.Count > 0 ? L.T(" — appeared after the plan; a new plan is needed") : string.Empty;
        sb.AppendLine(L.T($"New files in the source under moved folders: {Ui(r.NewInSource.Count)}{newNote}"));
        sb.AppendLine(r.Ok ? L.T("Result: everything moved is in place.") : L.T("Result: there are differences (verify report, \"Need attention\" filter)."));
        return sb.ToString();
    }

    /// <summary>The verify report: files that did not pass and new files in the source.</summary>
    public static IEnumerable<IReadOnlyList<string?>> VerifyRows(StateStore store, long applyRun, VerifyReport? report)
    {
        yield return ["kind", "source_path", "new_path", "batch", "detail"];
        foreach (var f in store.QueryFiles(applyRun, new FileFilter { Statuses = ["failed:verify"] }, 0, int.MaxValue))
            yield return ["not_verified", f.Path, null, null, f.Error];
        foreach (var (seq, path) in report?.NewInSource ?? [])
            yield return ["new_in_source", path, null, seq.ToString(CultureInfo.InvariantCulture), L.T("appeared in the source after the plan")];
    }

    /// <summary>
    /// MAP-8: rules "old prefix → new prefix" for SPOReplaceLink, as absolute decoded URLs: a folder moved whole gives
    /// one rule; files moved one by one (files batches, folders moved in part) give a rule each. What did not move gets
    /// no rule, so links to it are left alone.
    /// </summary>
    public static IEnumerable<IReadOnlyList<string?>> RuleRows(StateStore store, long applyRun)
    {
        var run = store.GetRun(applyRun) ?? throw new KeyNotFoundException(L.T($"Run {applyRun} not found."));
        if (run.Mode != RunModes.Apply)
            throw new InvalidOperationException(L.T($"Run {applyRun} is not a move (apply)."));
        var urls = store.GetTargets(applyRun).Where(t => t.DriveUrl is not null)
            .ToDictionary(t => t.Index, t => Uri.UnescapeDataString(t.DriveUrl!).TrimEnd('/'));
        yield return ["old", "new", "name"];
        foreach (var (seq, folder, dest, source, target) in store.MovedUnits(applyRun))
        {
            if (!urls.TryGetValue(0, out string? from) || !urls.TryGetValue(dest, out string? to))
                continue;
            yield return [from + "/" + source, to + (target.Length == 0 ? string.Empty : "/" + target), RuleName(applyRun, seq, folder)];
        }
    }

    public static void Write(StateStore store, long applyRun, string name, string format, Stream output, VerifyReport? report = null)
    {
        if (IsText(name))
        {
            byte[] text = Encoding.UTF8.GetBytes(PilotReport.Text(store, applyRun));
            output.Write(Encoding.UTF8.GetPreamble());
            output.Write(text);
            return;
        }
        var rows = name switch
        {
            "verify" => VerifyRows(store, applyRun, report),
            "rules" => RuleRows(store, applyRun),
            _ => throw new ArgumentException(L.T($"Unknown report \"{name}\"."), nameof(name)),
        };
        if (format == "xlsx")
            SimpleXlsxWriter.Write(output, name, rows);
        else
            ReportExporter.WriteCsv(output, rows);
    }

    private static string RuleName(long applyRun, int seq, bool folder) => folder
        ? L.T($"SPOMover run {applyRun} batch {seq}")
        : L.T($"SPOMover run {applyRun} batch {seq} file");

    private static string Ui(long n) => n.ToString("N0", L.Culture);
}
